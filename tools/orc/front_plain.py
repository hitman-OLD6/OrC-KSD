"""行式 Front（D2.4）：纯文本、无 ANSI、可完整重定向到文件。

每条命令后打印「命令输出 → 自上次以来的新日志 → 一块自包含战场快照」。
推送到达只入日志 deque，不插队打印（避免切碎输出块）；``--stream`` 才实时打印。
"""

import sys
import threading
import time


class PlainFront(object):
    def __init__(self, stdin=None, stdout=None):
        self.stdin = stdin or sys.stdin
        self.stdout = stdout or sys.stdout
        self.game = None
        self._stream = False
        self._stream_stop = threading.Event()
        self._print_lock = threading.Lock()

    def attach(self, game):
        self.game = game
        self._stream = bool(getattr(game.opts, "stream", False))

    # ------------------------------------------------------------ 输出

    def _write(self, lines):
        if not lines:
            return
        with self._print_lock:
            for line in lines:
                self.stdout.write(line + "\n")
            self.stdout.flush()

    def _width(self):
        if not self._isatty():
            return 96
        try:
            import shutil
            return max(60, min(120, shutil.get_terminal_size((96, 24)).columns))
        except Exception:  # noqa: BLE001
            return 96

    def _print_block(self, result):
        self._write(result.lines)
        self._write(self.game.new_log_lines())
        self._write(self.game.board_lines(width=self._width(), log_lines=None))

    # ------------------------------------------------------------ 主循环

    def run(self):
        game = self.game
        self._write(game.banner_lines())
        self._write(game.new_log_lines())
        self._write(game.board_lines(width=self._width(), log_lines=None))
        if self._stream:
            thread = threading.Thread(target=self._stream_loop, name="plain-stream")
            thread.daemon = True
            thread.start()
        try:
            while True:
                if self._isatty():
                    self.stdout.write("> ")
                    self.stdout.flush()
                raw = self.stdin.readline()
                if raw == "":
                    return self._on_eof()
                line = raw.rstrip("\r\n")
                result = game.handle(line)
                self._print_block(result)
                if result.exit_code is not None:
                    return result.exit_code
                if game.session.mode == "play":
                    game.runtime.touch_record()
        finally:
            self._stream_stop.set()

    def _isatty(self):
        try:
            return bool(self.stdout.isatty() and self.stdin.isatty())
        except Exception:  # noqa: BLE001
            return False

    def _on_eof(self):
        """D16.7：管道 EOF 且有 pending ⇒ 先 target.cancel 清理，再以非零码退出。"""
        game = self.game
        if game.session.pending:
            result = game.handle("x")
            self._write(result.lines)
            self._write(["（输入结束：已取消悬挂的选择请求）"])
            return 3
        return 1 if game.engine.last_failed or (game.exit_code or 0) else 0

    def _stream_loop(self):
        while not self._stream_stop.is_set():
            time.sleep(0.4)
            try:
                lines = self.game.new_log_lines()
            except Exception:  # noqa: BLE001
                return
            if lines:
                self._write(["· " + line if not line.startswith(("·", "⚠", "—")) else line
                             for line in lines])
