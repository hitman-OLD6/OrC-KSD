"""OrC 人类 CLI 客户端（play 对局 / watch 全知观战）。

核心与 plain 路径**零第三方依赖**；只有 ``front_tui`` 需要 ``prompt_toolkit``。
"""

__version__ = "1.0.0"
__all__ = ["cli", "commands", "manager", "render", "session", "timeouts", "transport"]
