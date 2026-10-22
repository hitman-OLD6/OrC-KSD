"""薄入口（D13.6）：直接执行 或 ``python -m orc`` 均可。

用法：
  python tools/orc_play.py play            # 与 AI 打一局
  python tools/orc_play.py play --plain    # 行式
  python tools/orc_play.py watch --instance cli-xxxxxxxx
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from orc.cli import main  # noqa: E402


def run():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001 - 重定向/老解释器时不强求
        pass
    return main(sys.argv[1:])


if __name__ == "__main__":
    raise SystemExit(run())
