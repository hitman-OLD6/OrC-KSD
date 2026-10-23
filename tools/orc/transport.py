"""传输层：NDJSON / JSON-RPC 帧编解码 ＋ Dispatcher（D2.1/D2.2/D11）。

三条时间线（D2.1）里这里只承担两条：
  * 读线程（每连接一条）——独占 ``recv``，切帧后分流：带 ``id`` 的投响应 Future，
    ``notify/target.request`` 并入待决（按 ``sessionId`` 去重），其余推送交 ``on_frame``；
  * 动作 worker（由 Front 提供）——所有 ``tools/call`` 串行经过 ``call_tool``，写侧持一把输出锁。

UI 线程永远不碰 socket。
"""

import json
import socket
import threading
import time

from . import timeouts


# ---------------------------------------------------------------- 异常


class DispatcherError(Exception):
    """传输/协议层可读异常基类（带受控 ``code``）。"""

    def __init__(self, code, message):
        Exception.__init__(self, message)
        self.code = code
        self.message = message

    def __str__(self):
        return self.message


class ConnectionLost(DispatcherError):
    def __init__(self, message):
        DispatcherError.__init__(self, "connection-lost", message)


class CallTimeout(DispatcherError):
    """等待响应超时。语义＝**已发出**（D11.6），调用方不得据此重发。"""

    def __init__(self, method, timeout):
        DispatcherError.__init__(
            self, "timeout", "等待 %s 响应超时（%.0fs）" % (method, timeout))


class RpcError(DispatcherError):
    """协议层 ``error`` 帧（解析错误/未知方法/参数类型错误）。"""


# ---------------------------------------------------------------- 结果


class ToolResult(object):
    """``tools/call`` 的统一返回外壳 ``{ok, code, message, details, engineResult}``。"""

    def __init__(self, payload=None):
        payload = payload if isinstance(payload, dict) else {}
        self.raw = payload
        self.ok = bool(payload.get("ok"))
        self.code = payload.get("code") or ("ok" if self.ok else "unknown")
        self.message = payload.get("message") or ""
        self.details = payload.get("details") or {}
        self.engine = payload.get("engineResult") or {}

    @property
    def pending(self):
        pending = self.details.get("pending") if isinstance(self.details, dict) else None
        return pending if isinstance(pending, dict) else None

    @property
    def is_targeting(self):
        return self.code == "targeting" or self.pending is not None

    def __repr__(self):
        return "<ToolResult ok=%s code=%s>" % (self.ok, self.code)


# ---------------------------------------------------------------- 接口


ACTION = "action"
READ = "read"


class IDispatcher(object):
    """协议交互的唯一出口（D2.6：单测用 FakeDispatcher 注入帧序列）。"""

    def __init__(self):
        self.on_frame = None      # Callable[[str, dict], None]
        self.on_pending = None    # Callable[[dict|None], None]
        self.on_disconnect = None  # Callable[[str], None]
        self.connected = False
        self.last_inbound = time.time()

    # -- 生命周期 --
    def connect(self):
        raise NotImplementedError

    def close(self):
        self.connected = False

    # -- 请求 --
    def request(self, method, params=None, timeout=None, retries=0):
        raise NotImplementedError

    def call_tool(self, name, arguments=None, kind=ACTION, timeout=None):
        """串行调用工具；只有 ``room-rotating`` 与 ``busy`` 会自动重试（D11.8）。"""
        timeout = timeout or (timeouts.ACTION_CALL if kind == ACTION else timeouts.READ_CALL)
        retries_left = timeouts.READ_RETRIES if kind == READ else 0
        rotating_used = 0
        busy_used = 0
        budget_start = time.time()
        while True:
            try:
                result = self._invoke(name, arguments, timeout, kind)
            except CallTimeout:
                if retries_left > 0:
                    retries_left -= 1
                    continue
                raise
            if result.code == "room-rotating" and rotating_used < len(timeouts.ROOM_ROTATING_RETRY_MS):
                if time.time() - budget_start < timeouts.ROOM_ROTATING_BUDGET:
                    delay = timeouts.ROOM_ROTATING_RETRY_MS[rotating_used] / 1000.0
                    rotating_used += 1
                    time.sleep(delay)
                    continue
            if result.code == "busy" and busy_used < timeouts.BUSY_RETRIES:
                busy_used += 1
                time.sleep(timeouts.BUSY_RETRY)
                continue
            return result

    def _invoke(self, name, arguments, timeout, kind):
        payload = self.request(
            "tools/call", {"name": name, "arguments": arguments or {}}, timeout)
        return ToolResult(payload)

    # -- 待决 --
    def peek_pending(self):
        raise NotImplementedError

    def wait_pending(self, session_id=None, timeout=None):
        raise NotImplementedError

    def clear_pending(self, session_id=None):
        raise NotImplementedError


# ---------------------------------------------------------------- 真实现


class SocketDispatcher(IDispatcher):
    """单连接 Dispatcher：一读线程 ＋ 写锁 ＋ 响应 Future 表 ＋ 待决条件变量。"""

    def __init__(self, host, port, label="orc", verbose=False):
        IDispatcher.__init__(self)
        self.host = host
        self.port = int(port)
        self.label = label
        self.verbose = verbose
        self._sock = None
        self._buffer = b""
        self._out_lock = threading.Lock()
        self._id_lock = threading.Lock()
        self._next_id = 0
        self._responses = {}          # id -> [threading.Event, frame|None, expired]
        self._resp_lock = threading.Lock()
        self._orphans = {}            # 迟到的响应（超时后到达）
        self._pending = {}            # sessionId -> pending（去重，F14）
        self._pending_lock = threading.Lock()
        self._pending_cond = threading.Condition(self._pending_lock)
        self._closed = threading.Event()
        self._reader = None
        self._heart = None
        self._heartbeat_busy = False
        self._quiet = False           # 置位＝由本侧主动关闭，读线程不再重复报断线

    # -- 连接 --
    def connect(self):
        try:
            self._sock = socket.create_connection((self.host, self.port), timeouts.CONNECT)
        except OSError as exc:
            raise ConnectionLost("连不上 %s:%s（%s）" % (self.host, self.port, exc))
        self._sock.settimeout(None)
        self.connected = True
        self.last_inbound = time.time()
        self._reader = threading.Thread(target=self._read_loop, name="orc-reader")
        self._reader.daemon = True
        self._reader.start()
        self._heart = threading.Thread(target=self._heartbeat_loop, name="orc-heartbeat")
        self._heart.daemon = True
        self._heart.start()
        return self

    @property
    def endpoint(self):
        return "%s:%s" % (self.host, self.port)

    def close(self):
        if self._closed.is_set():
            return
        self._quiet = True
        self._closed.set()
        self.connected = False
        try:
            if self._sock is not None:
                self._sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            if self._sock is not None:
                self._sock.close()
        except OSError:
            pass
        with self._pending_cond:
            self._pending_cond.notify_all()
        with self._resp_lock:
            for entry in self._responses.values():
                entry[0].set()

    # -- 读线程 --
    def _read_loop(self):
        reason = "连接已被关闭"
        while not self._closed.is_set():
            try:
                chunk = self._sock.recv(65536)
            except OSError as exc:
                reason = "读取失败：%s" % exc
                break
            if not chunk:
                reason = "服务端关闭了连接"
                break
            self.last_inbound = time.time()
            self._buffer += chunk
            while b"\n" in self._buffer:
                line, self._buffer = self._buffer.split(b"\n", 1)
                line = line.strip()
                if not line:
                    continue
                try:
                    frame = json.loads(line.decode("utf-8"))
                except ValueError:
                    if self.verbose:
                        print("[warn] 无法解析的帧：%r" % line[:120])
                    continue
                self._dispatch(frame)
        self.connected = False
        if not self._quiet:
            self._notify_disconnect(reason)

    def _notify_disconnect(self, reason):
        callback = self.on_disconnect
        if callback is not None:
            try:
                callback(reason)
            except Exception:  # noqa: BLE001 - 回调故障不得反噬读线程
                pass

    def _dispatch(self, frame):
        if "id" in frame and ("result" in frame or "error" in frame):
            self._resolve(frame)
            return
        method = frame.get("method") or ""
        params = frame.get("params")
        if not isinstance(params, dict):
            params = {}
        if method == "notify/target.request":
            self._ingest_pending(params)
            return
        if method == "notify/target.expired":
            self._expire_pending(params)
            return
        callback = self.on_frame
        if callback is not None:
            try:
                callback(method, params)
            except Exception:  # noqa: BLE001
                pass

    def _resolve(self, frame):
        key = str(frame.get("id"))
        with self._resp_lock:
            entry = self._responses.pop(key, None)
            if entry is None:
                self._orphans[key] = frame
                if len(self._orphans) > 32:
                    self._orphans.pop(sorted(self._orphans)[0], None)
                return
        entry[1] = frame
        entry[0].set()

    def _ingest_pending(self, pending):
        session_id = pending.get("sessionId")
        if not session_id:
            return
        with self._pending_cond:
            self._pending[session_id] = pending
            self._pending_cond.notify_all()
        callback = self.on_pending
        if callback is not None:
            try:
                callback(pending)
            except Exception:  # noqa: BLE001
                pass

    def _expire_pending(self, payload):
        session_id = payload.get("sessionId")
        if not session_id:
            return
        with self._pending_cond:
            self._pending.pop(session_id, None)
            self._pending_cond.notify_all()
        callback = self.on_pending
        if callback is not None:
            try:
                callback(None)
            except Exception:  # noqa: BLE001
                pass

    # -- 写/请求 --
    def _send(self, frame):
        data = (json.dumps(frame, ensure_ascii=False) + "\n").encode("utf-8")
        try:
            with self._out_lock:
                self._sock.sendall(data)
        except OSError as exc:
            self.connected = False
            raise ConnectionLost("写入失败：%s" % exc)

    def request(self, method, params=None, timeout=None, retries=0):
        timeout = timeouts.READ_CALL if timeout is None else timeout
        attempt = 0
        while True:
            try:
                return self._request_once(method, params, timeout)
            except CallTimeout:
                # 连接层失败＝未发出可重试；等待超时＝已发出（D11.6），交给调用方判定
                if attempt >= retries:
                    raise
                attempt += 1

    def _request_once(self, method, params, timeout):
        if not self.connected:
            raise ConnectionLost("连接已断开（%s）" % self.endpoint)
        with self._id_lock:
            self._next_id += 1
            rid = str(self._next_id)
        frame = {"jsonrpc": "2.0", "id": rid, "method": method}
        if params is not None:
            frame["params"] = params
        entry = [threading.Event(), None, False]
        with self._resp_lock:
            self._responses[rid] = entry
        self._send(frame)
        if not entry[0].wait(timeout):
            with self._resp_lock:
                entry[2] = True
                self._responses.pop(rid, None)
            raise CallTimeout(method, timeout)
        reply = entry[1] or {}
        if "error" in reply:
            error = reply.get("error") or {}
            raise RpcError(str(error.get("code")), str(error.get("message")))
        result = reply.get("result")
        # 结果既可能是对象（initialize/session.attach/tools/call），也可能是数组（tools/list）——
        # 早先只放行 dict，导致 tools/list 永远返回空表（客户端静默丢掉数组）。
        return {} if result is None else result

    # -- 待决 --
    def peek_pending(self):
        with self._pending_lock:
            for pending in self._pending.values():
                return pending
        return None

    def wait_pending(self, session_id=None, timeout=None):
        timeout = timeouts.PENDING_WAIT if timeout is None else timeout
        deadline = time.time() + timeout
        with self._pending_cond:
            while True:
                if session_id is not None and session_id in self._pending:
                    return self._pending[session_id]
                if session_id is None and self._pending:
                    return list(self._pending.values())[0]
                left = deadline - time.time()
                if left <= 0:
                    return None
                self._pending_cond.wait(min(left, 0.2))

    def clear_pending(self, session_id=None):
        with self._pending_cond:
            if session_id is None:
                self._pending.clear()
            else:
                self._pending.pop(session_id, None)
            self._pending_cond.notify_all()

    # -- 心跳（D11.1：空闲 20s 发 ping，两次无响应判断线）--
    def _heartbeat_loop(self):
        misses = 0
        while not self._closed.is_set():
            time.sleep(1.0)
            if self._closed.is_set() or not self.connected or self._heartbeat_busy:
                continue
            if time.time() - self.last_inbound < timeouts.HEARTBEAT_IDLE:
                continue
            self._heartbeat_busy = True
            try:
                self.request("ping", None, timeouts.HEARTBEAT_MISS)
                misses = 0
            except CallTimeout:
                misses += 1
            except (ConnectionLost, RpcError):
                self._close_quietly()
                return
            finally:
                self._heartbeat_busy = False
            if misses >= 2:
                self._close_quietly()
                return

    def _close_quietly(self):
        self.close()
        self._notify_disconnect("心跳无响应（连续两次）")
