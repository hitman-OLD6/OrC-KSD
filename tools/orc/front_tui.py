"""全屏 Front（D2.4）：prompt_toolkit Application ＋ HSplit。

**唯一** import ``prompt_toolkit`` 的模块；行式路径永不导入它（D13.5）。
布局自上而下：战场（对手区/我方区/手牌）／日志窗格（自绘最近 N 行）／输入行／提示条。
渲染由推送驱动：``on_frame → loop.call_soon_threadsafe(app.invalidate)``。
"""

import threading

from prompt_toolkit import Application
from prompt_toolkit.buffer import Buffer
from prompt_toolkit.completion import Completer, Completion
from prompt_toolkit.formatted_text import FormattedText
from prompt_toolkit.key_binding import KeyBindings
from prompt_toolkit.layout import HSplit, Layout, Window
from prompt_toolkit.layout.controls import BufferControl, FormattedTextControl
from prompt_toolkit.layout.dimension import Dimension
from prompt_toolkit.styles import Style


STYLE = Style.from_dict({
    "board": "",
    "header": "bold",
    "log": "ansigray",
    "notice": "bold",
    "prompt": "bold",
    # 提示条由 bottom_toolbar 承载：深底浅字，避免默认浅灰底在暗色终端里刺眼。
    "bottom-toolbar": "bg:#262626 #ffffff",
})


class _EngineCompleter(Completer):
    """Tab 补全：条目含中文卡名与关键数值（D16.2）；只填参、不执行（D16.4）。"""

    def __init__(self, game):
        self.game = game

    def get_completions(self, document, complete_event):
        text = document.text_before_cursor
        candidates = self.game.engine.candidates_for(text)
        if not candidates:
            return
        stripped = text.rstrip()
        prefix = "" if stripped.endswith(" ") else (stripped.split()[-1] if stripped.split() else "")
        for value, display in candidates:
            yield Completion(value, start_position=-len(prefix), display=display)


class TuiFront(object):
    def __init__(self, stdin=None, stdout=None):
        self.stdin = stdin
        self.stdout = stdout
        self.game = None
        self.app = None
        self.buffer = None
        self._output = []
        self._lock = threading.Lock()
        self._log_count = 0
        self._notice_lines = []

    def attach(self, game):
        self.game = game

    # ------------------------------------------------------------ 布局

    def _size_columns(self):
        try:
            return max(60, self.app.output.get_size().columns)
        except Exception:  # noqa: BLE001
            return 96

    def _board_text(self):
        width = self._size_columns()
        lines = self.game.board_lines(width=width, log_lines=None)
        fragments = []
        for line in lines:
            if line.startswith(("==", "--")):
                fragments.append(("class:header", line + "\n"))
            elif line.startswith("["):
                fragments.append(("class:notice", line + "\n"))
            else:
                fragments.append(("class:board", line + "\n"))
        return FormattedText(fragments)

    def _log_rows(self):
        """日志窗格能拿到几行＝终端总行数 − 战场行数 − 分隔线/输入行/提示条。"""
        try:
            total = self.app.output.get_size().rows
        except Exception:  # noqa: BLE001
            total = 30
        try:
            board = len(self.game.board_lines(width=self._size_columns(), log_lines=None))
        except Exception:  # noqa: BLE001
            board = 16
        return max(3, total - board - 3)

    def _log_text(self):
        width = self._size_columns()
        with self._lock:
            lines = list(self._output[-self._log_rows():])
        if not lines:
            lines = ["（日志）"]
        fragments = []
        for line in lines:
            text = line if len(line) <= width - 2 else line[:width - 3] + "…"
            fragments.append(("class:log", " " + text + "\n"))
        return FormattedText(fragments)

    def _notice_text(self):
        snap = self.game.session.snapshot()
        from . import render
        return FormattedText([("class:notice", " " + render._notice(snap) + " ")])

    # ------------------------------------------------------------ 输入

    def _submit(self, buffer):
        line = buffer.text
        buffer.text = ""
        if line.strip():
            thread = threading.Thread(target=self._run_line, args=(line,), name="tui-action")
            thread.daemon = True
            thread.start()
        return False

    def _run_line(self, line):
        try:
            result = self.game.handle(line)
        except Exception as exc:  # noqa: BLE001 - 界面线程不得因单条命令崩掉
            self._append(["✗ 命令执行失败：%s" % exc])
            self._invalidate()
            return
        self._append(result.lines)
        new_lines = self.game.new_log_lines()
        self._append(new_lines)
        self._invalidate()
        if result.exit_code is not None:
            self._exit(result.exit_code)

    def _append(self, lines):
        if not lines:
            return
        with self._lock:
            self._output.extend(lines)

    def _invalidate(self):
        try:
            self.app.loop.call_soon_threadsafe(self.app.invalidate)
        except Exception:  # noqa: BLE001
            pass

    def _exit(self, code):
        self.exit_code = code

        def stop():
            try:
                self.app.exit()
            except Exception:  # noqa: BLE001
                pass

        try:
            self.app.loop.call_soon_threadsafe(stop)
        except Exception:  # noqa: BLE001
            pass

    # ------------------------------------------------------------ 运行

    def run(self):
        game = self.game
        self._append(game.banner_lines())
        self._append(game.new_log_lines())
        self.exit_code = None

        bindings = KeyBindings()

        @bindings.add("c-c")
        def _clear(event):
            event.current_buffer.text = ""

        @bindings.add("tab")
        def _complete(event):
            event.current_buffer.start_completion()

        @bindings.add("c-d")
        def _eof(event):
            event.current_buffer.text = ""
            thread = threading.Thread(target=self._run_line, args=("quit",), daemon=True)
            thread.start()

        self.buffer = Buffer(
            name="input",
            multiline=False,
            completer=_EngineCompleter(game),
            complete_while_typing=False,
            accept_handler=self._submit,
        )

        body = HSplit([
            # 战场按自身内容高度（不扩展），日志窗口吃掉剩余空间；
            # 提示条交给 bottom_toolbar——它由框架固定在最后一行，不会被挤出屏幕。
            Window(FormattedTextControl(self._board_text), wrap_lines=False,
                   dont_extend_height=True, style="class:board"),
            Window(FormattedTextControl(self._log_text), height=Dimension(weight=1, min=3),
                   wrap_lines=False, style="class:log"),
            Window(height=1, char="-", style="class:log"),
            Window(BufferControl(buffer=self.buffer), height=1, style="class:prompt"),
            # 提示条是 HSplit 的最后一个固定行窗口：Application 没有 bottom_toolbar 参数
            # （那是 PromptSession 的），这里用 height=1 + 固定样式实现同样的"状态栏"。
            Window(FormattedTextControl(self._notice_text), height=1,
                   style="class:bottom-toolbar"),
        ])

        self.app = Application(
            layout=Layout(body, focused_element=self.buffer),
            key_bindings=bindings,
            full_screen=True,
            mouse_support=False,
            style=STYLE,
        )

        # 推送驱动重绘（D2.4）
        dispatcher = game.dispatcher
        original = dispatcher.on_frame

        def on_frame(method, params):
            if original is not None:
                original(method, params)
            self._invalidate()

        dispatcher.on_frame = on_frame

        self.app.run()
        return self.exit_code if self.exit_code is not None else 0
