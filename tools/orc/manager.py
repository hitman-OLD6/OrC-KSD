"""管理器面：探活 / 拉起 / spawn / admin / kill ＋ 卡包发现 ＋ ``session.json`` 记录（D3/D4/D5）。

管理器是**外部常驻依赖**：本模块绝不 kill 管理器（D4.4）；实例的生死才是本会话的事。
"""

import glob
import json
import os
import shutil
import socket
import subprocess
import sys
import time
import uuid

from . import timeouts


DEFAULT_ENDPOINT = "127.0.0.1:51999"
DEFAULT_PACK = os.path.join("samples", "card-pack-ai")
MANAGER_DLL = os.path.join("artifacts", "slot", "bin", "Orc.Manager", "debug", "Orc.Manager.dll")
SERVER_DLL = os.path.join("artifacts", "slot", "bin", "Orc.Manager", "debug", "Orc.Server.dll")
SESSION_SCHEMA = 1
DECK_SIZE = 39


class CliError(Exception):
    """启动/编排阶段的可读错误（含下一步建议）。"""

    def __init__(self, message, hint=""):
        Exception.__init__(self, message)
        self.message = message
        self.hint = hint

    def __str__(self):
        return self.message + (("\n" + self.hint) if self.hint else "")


# ---------------------------------------------------------------- 路径


def find_repo_root(start=None):
    current = os.path.abspath(start or os.path.dirname(os.path.abspath(__file__)))
    while True:
        if os.path.exists(os.path.join(current, "OrcEngine.sln")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    return os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))


def default_session_file(repo_root):
    return os.path.join(repo_root, ".orc-manager", "cli", "session.json")


def default_log_path(repo_root, port):
    return os.path.join(repo_root, ".orc-manager", "logs", "manager-%s.log" % port)


# ---------------------------------------------------------------- 记录（D3.2）


def load_record(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            record = json.load(handle)
    except (IOError, OSError):
        return None
    except ValueError:
        return None
    if not isinstance(record, dict) or not record.get("instanceId"):
        return None
    return record


def save_record(path, record):
    record = dict(record)
    record["schemaVersion"] = SESSION_SCHEMA
    record["updatedAt"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    directory = os.path.dirname(path)
    if directory and not os.path.isdir(directory):
        os.makedirs(directory)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(record, handle, ensure_ascii=False, indent=2)
    os.replace(tmp, path)
    return record


def delete_record(path):
    try:
        os.remove(path)
    except (IOError, OSError):
        pass


def touch_record(path, record):
    """只更新 ``updatedAt``（节流 ≥2s，D3.4）。"""
    try:
        last = os.path.getmtime(path)
    except OSError:
        last = 0
    if time.time() - last < 2.0:
        return
    try:
        save_record(path, record)
    except (IOError, OSError):
        pass


def new_instance_id():
    return "cli-%s" % uuid.uuid4().hex[:8]


# ---------------------------------------------------------------- 管理器客户端


class ManagerClient(object):
    def __init__(self, endpoint=None, verbose=False):
        endpoint = endpoint or DEFAULT_ENDPOINT
        if ":" not in endpoint:
            raise CliError("--manager 需要 host:port 形式（收到 '%s'）" % endpoint)
        host, port = endpoint.rsplit(":", 1)
        self.host = host
        self.port = int(port)
        self.verbose = verbose
        self._sock = None
        self._buffer = b""
        self._next_id = 0

    @property
    def endpoint(self):
        return "%s:%s" % (self.host, self.port)

    # -- 连接 --
    def _connect(self, timeout=None):
        if self._sock is not None:
            return self._sock
        try:
            sock = socket.create_connection((self.host, self.port), timeout or timeouts.CONNECT)
        except OSError as exc:
            raise CliError("管理器未运行（连不上 %s：%s）" % (self.endpoint, exc))
        sock.settimeout(None)
        self._sock = sock
        self._buffer = b""
        return sock

    def close(self):
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
            self._sock = None
            self._buffer = b""

    def _readline(self, deadline):
        while b"\n" not in self._buffer:
            left = deadline - time.time()
            if left <= 0:
                raise CliError("管理器 %s 响应超时" % self.endpoint)
            self._sock.settimeout(min(left, 1.0))
            try:
                chunk = self._sock.recv(65536)
            except socket.timeout:
                continue
            except OSError as exc:
                raise CliError("管理器连接中断：%s" % exc)
            if not chunk:
                raise CliError("管理器 %s 关闭了连接" % self.endpoint)
            self._buffer += chunk
        line, self._buffer = self._buffer.split(b"\n", 1)
        return line

    def call(self, method, params=None, timeout=None):
        timeout = timeout or timeouts.MANAGER_CALL
        self._connect()
        self._next_id += 1
        rid = str(self._next_id)
        frame = {"jsonrpc": "2.0", "id": rid, "method": method}
        if params is not None:
            frame["params"] = params
        try:
            self._sock.sendall((json.dumps(frame, ensure_ascii=False) + "\n").encode("utf-8"))
        except OSError as exc:
            self.close()
            raise CliError("向管理器写入失败：%s" % exc)
        deadline = time.time() + timeout
        while time.time() < deadline:
            raw = self._readline(deadline)
            raw = raw.strip()
            if not raw:
                continue
            try:
                reply = json.loads(raw.decode("utf-8"))
            except ValueError:
                continue
            if str(reply.get("id")) != rid:
                continue
            if "error" in reply:
                error = reply.get("error") or {}
                raise CliError("管理器报错：%s" % error.get("message"))
            result = reply.get("result")
            return result if isinstance(result, dict) else {}
        raise CliError("管理器 %s 响应超时（%s）" % (self.endpoint, method))

    # -- 方法 --
    def ping(self):
        try:
            result = self.call("manager.ping", None, timeout=2.0)
        except CliError:
            return False
        return bool(result.get("ok"))

    def list_instances(self):
        result = self.call("manager.list")
        if not result.get("ok"):
            raise CliError("manager.list 失败：%s" % (result.get("message") or result.get("code")))
        payload = result.get("payload") or {}
        return list(payload.get("instances") or [])

    def find(self, instance_id):
        for item in self.list_instances():
            if item.get("instanceId") == instance_id:
                return item
        return None

    def spawn(self, instance_id, label, pack_dirs, seats=None):
        params = {
            "instanceId": instance_id,
            "label": label,
            "mode": "play",
            "deckRules": "none",
            "packDirectories": list(pack_dirs),
            "seats": seats or [
                {"kind": "agent", "displayName": "人类玩家"},
                {"kind": "ai", "displayName": "AI"},
            ],
            "port": 0,
        }
        return self.call("manager.spawn", params, timeout=timeouts.SPAWN_CALL)

    def admin(self, instance_id, op, args=None):
        return self.call("manager.admin", {
            "instanceId": instance_id,
            "op": op,
            "args": args or {},
        }, timeout=timeouts.MANAGER_CALL)

    def admin_wait(self, instance_id, op, args=None, timeout=None):
        return self.call("manager.admin", {
            "instanceId": instance_id,
            "op": op,
            "args": args or {},
        }, timeout=timeout or timeouts.START_CALL)

    def kill(self, instance_id):
        try:
            return self.call("manager.kill", {"instanceId": instance_id})
        except CliError as exc:
            return {"ok": False, "code": "manager-error", "message": str(exc)}


# ---------------------------------------------------------------- 拉起（D4）


def preflight(repo_root):
    """只复刻会真正致命的三项（D4.6）。"""
    problems = []
    if shutil.which("dotnet") is None:
        if sys.platform.startswith("win"):
            problems.append("找不到 dotnet（请安装 .NET 8 运行时，或确认 dotnet 在 PATH 中）")
        else:
            problems.append("找不到 dotnet（请安装 .NET 8 运行时，或确认 dotnet 在 PATH 中）")
    manager_dll = os.path.join(repo_root, MANAGER_DLL)
    server_dll = os.path.join(repo_root, SERVER_DLL)
    if not os.path.exists(manager_dll):
        problems.append("缺少 %s" % manager_dll)
    if not os.path.exists(server_dll):
        problems.append("缺少 %s（必须与 Orc.Manager.dll 同目录）" % server_dll)
    if problems:
        raise CliError(
            "管理器无法启动：" + "；".join(problems),
            "修复：dotnet build OrcEngine.sln",
        )
    return manager_dll


def _truncate_log(path, limit=5 * 1024 * 1024):
    try:
        size = os.path.getsize(path)
    except OSError:
        return
    if size <= limit:
        return
    try:
        with open(path, "rb") as handle:
            handle.seek(-limit, os.SEEK_END)
            tail = handle.read()
        with open(path, "wb") as handle:
            handle.write(tail)
    except (IOError, OSError):
        pass


def launch_manager(repo_root, port):
    """detached 拉起 ``serve``（不解析就绪行，就绪只认 ping；D4.2/D4.3）。"""
    manager_dll = preflight(repo_root)
    log_path = default_log_path(repo_root, port)
    log_dir = os.path.dirname(log_path)
    if not os.path.isdir(log_dir):
        os.makedirs(log_dir)
    _truncate_log(log_path)
    handle = open(log_path, "ab")
    kwargs = {
        "cwd": repo_root,
        "stdin": subprocess.DEVNULL,
        "stdout": handle,
        "stderr": handle,
        "close_fds": True,
    }
    if sys.platform.startswith("win"):
        flags = 0
        flags |= getattr(subprocess, "DETACHED_PROCESS", 0x00000008)
        flags |= getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0x00000200)
        kwargs["creationflags"] = flags
    else:
        kwargs["start_new_session"] = True
    try:
        process = subprocess.Popen(
            [shutil.which("dotnet") or "dotnet", manager_dll, "serve", "--port", str(port)],
            **kwargs
        )
    except OSError as exc:
        handle.close()
        raise CliError("拉起管理器失败：%s" % exc)
    handle.close()
    return process, log_path


def tail_log(path, count=30):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            lines = handle.read().splitlines()
    except (IOError, OSError):
        return []
    return lines[-count:]


def ensure_manager(client, launch, repo_root, say=None):
    """统一顺序（D4.5）：先 ping → 成功即复用；失败且 --launch-manager 才拉起。"""
    if client.ping():
        return "reused"
    if not launch:
        raise CliError(
            "管理器未运行（%s 无响应）" % client.endpoint,
            "启动方式：\n"
            "  1) start-manager.bat\n"
            "  2) orc_play.bat play --launch-manager\n"
            "  3) dotnet artifacts/slot/bin/Orc.Manager/debug/Orc.Manager.dll serve --port %s"
            % client.port,
        )
    process, log_path = launch_manager(repo_root, client.port)
    deadline = time.time() + timeouts.MANAGER_START
    while time.time() < deadline:
        if client.ping():
            if say:
                say("管理器由本次会话拉起，继续常驻：%s" % client.endpoint)
            return "launched"
        if process.poll() is not None:
            lines = tail_log(log_path)
            raise CliError(
                "管理器启动失败（exit=%s）" % process.returncode,
                "日志尾部（%s）：\n%s" % (log_path, "\n".join(lines) or "（空）"),
            )
        time.sleep(timeouts.MANAGER_PING_INTERVAL)
    lines = tail_log(log_path)
    raise CliError(
        "管理器启动超时（%.0fs 内 manager.ping 未成功）" % timeouts.MANAGER_START,
        "日志尾部（%s）：\n%s" % (log_path, "\n".join(lines) or "（空）"),
    )


# ---------------------------------------------------------------- 卡包与卡组（D5）


class Pack(object):
    def __init__(self, directory, package_id, version, guid, asset_ids):
        self.directory = directory
        self.package_id = package_id
        self.version = version
        self.guid = guid
        self.asset_ids = asset_ids

    @property
    def card_ids(self):
        """注册面使用的卡 id：``pack/<包 id>/<资产 id>``（``PackageRegistrar`` 的前缀）。"""
        prefix = "pack/%s/" % self.package_id
        return [prefix + asset_id for asset_id in self.asset_ids]

    def package_ref(self):
        return {"packageId": self.package_id, "version": self.version, "guid": self.guid}

    def deck(self, size=DECK_SIZE):
        ids = self.card_ids[:size]
        if not ids:
            return []
        return [ids[index % len(ids)] for index in range(size)]


def load_pack(directory):
    directory = os.path.abspath(directory)
    manifest_path = os.path.join(directory, "manifest.json")
    if not os.path.exists(manifest_path):
        raise CliError("卡包缺少 manifest.json：%s" % directory)
    with open(manifest_path, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    package_id = manifest.get("id")
    if not package_id:
        raise CliError("卡包 manifest 缺少 id：%s" % directory)

    found = set()
    metadata = manifest.get("metadata") or []
    if metadata:
        for relative in metadata:
            path = os.path.join(directory, relative)
            if not os.path.exists(path):
                continue
            try:
                with open(path, "r", encoding="utf-8") as handle:
                    meta = json.load(handle)
            except (IOError, OSError, ValueError):
                continue
            for asset in meta.get("assets") or []:
                if asset.get("kind") == "card" and asset.get("id"):
                    found.add(asset["id"])
    else:
        for path in glob.glob(os.path.join(directory, "**", "*.card.json"), recursive=True):
            if path.endswith(".meta.json"):
                continue
            name = os.path.basename(path)
            asset_id = name[:-len(".card.json")]
            if asset_id:
                found.add(asset_id)

    asset_ids = sorted(found)
    return Pack(directory, package_id, manifest.get("version"), manifest.get("guid"), asset_ids)


def load_deck_file(path):
    with open(path, "r", encoding="utf-8") as handle:
        deck = json.load(handle)
    if not isinstance(deck, list) or any(not isinstance(item, str) for item in deck):
        raise CliError("--deck 需要 JSON 字符串数组（%s）" % path)
    return deck
