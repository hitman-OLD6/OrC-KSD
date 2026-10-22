"""``python -m orc`` 入口（与 ``tools/orc_play.py`` 同一实现，D13.6）。"""

import os
import sys

if __package__ in (None, ""):  # 直接执行本文件时补 sys.path
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from orc.cli import main  # noqa: E402


def run():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:  # noqa: BLE001 - 老解释器/重定向时不强求
        pass
    return main(sys.argv[1:])


if __name__ == "__main__":
    raise SystemExit(run())
