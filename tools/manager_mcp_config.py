#!/usr/bin/env python3
"""把 orc-manager 条目 upsert 进宿主的 MCP 配置（默认 ~/.workbuddy/mcp.json）。

为什么不手写 JSON：这是**外部配置**，里面还有别人的条目（orc-seat1 / orc-orch 等），
手写/正则替换极易破坏。这里用 json 解析后定点 upsert，并在写之前留一份带时间戳的备份。

用法：
    python tools/manager_mcp_config.py [--port 51999] [--dll <path>] [--dry-run]

退出码：0 成功（或 dry-run 可写）；1 失败；2 无需变更（已是最新）。
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import time

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DLL = os.path.join(
    REPO_ROOT, "src", "Orc.Manager", "bin", "Debug", "net8.0", "Orc.Manager.dll"
)
DEFAULT_CONFIG = os.path.join(os.path.expanduser("~"), ".workbuddy", "mcp.json")
ENTRY_NAME = "orc-manager"


def to_forward_slashes(path: str) -> str:
    return path.replace("\\", "/")


def build_entry(dll: str, port: int) -> dict:
    return {
        "command": "dotnet",
        "args": [to_forward_slashes(dll), "mcp", "--connect", f"127.0.0.1:{port}"],
    }


def load_config(path: str) -> dict:
    if not os.path.exists(path):
        return {"mcpServers": {}}

    with open(path, "r", encoding="utf-8") as handle:
        text = handle.read().strip()

    if not text:
        return {"mcpServers": {}}

    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError(f"{path} 的顶层不是 JSON 对象。")

    servers = data.get("mcpServers")
    if not isinstance(servers, dict):
        data["mcpServers"] = {}
    return data


def main() -> int:
    parser = argparse.ArgumentParser(description="upsert orc-manager into MCP config")
    parser.add_argument("--port", type=int, default=51999)
    parser.add_argument("--dll", default=DEFAULT_DLL)
    parser.add_argument("--config", default=DEFAULT_CONFIG)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if not os.path.exists(args.dll):
        print(f"[FAIL] manager dll not found: {args.dll}", file=sys.stderr)
        print("       build first: dotnet build src/Orc.Manager/Orc.Manager.csproj", file=sys.stderr)
        return 1

    try:
        data = load_config(args.config)
    except (OSError, ValueError, json.JSONDecodeError) as ex:
        print(f"[FAIL] cannot read {args.config}: {ex}", file=sys.stderr)
        return 1

    entry = build_entry(args.dll, args.port)
    servers = data["mcpServers"]
    previous = servers.get(ENTRY_NAME)

    if previous == entry:
        print(f"[ok] {ENTRY_NAME} already up to date (port {args.port}).")
        return 2

    servers[ENTRY_NAME] = entry
    rendered = json.dumps(data, ensure_ascii=False, indent=2) + "\n"

    if args.dry_run:
        print(rendered)
        return 0

    os.makedirs(os.path.dirname(args.config), exist_ok=True)

    if os.path.exists(args.config):
        backup = f"{args.config}.bak.{time.strftime('%Y%m%d-%H%M%S')}"
        shutil.copy2(args.config, backup)
        print(f"[ok] backup: {backup}")

    with open(args.config, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(rendered)

    action = "updated" if previous is not None else "added"
    print(f"[ok] {action} '{ENTRY_NAME}' in {args.config} (port {args.port}).")
    print("[next] restart your host so it reloads MCP servers.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
