#!/usr/bin/env python3
"""ATTRIBUTION.md 轻量校验（零第三方依赖）。

只做一件事：把归因表里反引号内的用例令牌，拿去同一次全量跑出的 trx 里对名字。

- 令牌形如 `类名::方法名` 或 `类名::方法名(参数摘要)`；
- 归一化为 `类名.方法名` 后与 trx 的 testName 比对，命中条件：
    1. 相等；
    2. trx 全名以 `.类名.方法名` 结尾（归因表写了短名）；
    3. trx 全名以 `类名.方法名(` 开头，或包含 `.类名.方法名(`（`[Theory]` 行级）。
- 0 命中 ⇒ 报错（拼错名字／引用已删用例），退出码 1；
- 多命中 ⇒ 警告（`[Theory]` 建议写行级参数），不影响退出码。

不做语义校验：不检查分组是否合理、不检查成员是否属于 175 条。

用法：
    py -3.13 docs/测试失败清零/check_attribution.py
    py -3.13 docs/测试失败清零/check_attribution.py --trx artifacts/trx-attr-all
    py -3.13 docs/测试失败清零/check_attribution.py --trx a.trx b.trx --attribution 路径
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# 类名/方法名允许中文（本仓有 `抢修_…`、`csx制品_…` 这类用例名），允许类名带命名空间点号。
_IDENT = r"[A-Za-z_\u4e00-\u9fff][A-Za-z0-9_\u4e00-\u9fff.]*"
_TOKEN_RE = re.compile(r"`([^`\n]+?::[^`\n]+?)`")
_NAME_RE = re.compile(rf"^({_IDENT})::({_IDENT})(\(.*\))?$")

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_ATTRIBUTION = Path(__file__).resolve().parent / "ATTRIBUTION.md"


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def load_trx_names(paths: list[Path]) -> set[str]:
    """读 trx，返回全部 testName（含通过/失败/跳过）。"""
    names: set[str] = set()
    for path in paths:
        root = ET.parse(path).getroot()
        for el in root.iter():
            tag = _local(el.tag)
            if tag == "UnitTestResult":
                name = el.get("testName")
                if name:
                    names.add(name)
            elif tag == "TestMethod":
                # 兜底：结果节点缺 testName 时用定义面的 className/name 拼全名。
                cls, name = el.get("className"), el.get("name")
                if cls and name:
                    names.add(f"{cls}.{name}")
    return names


def find_trx(root: Path) -> list[Path]:
    """缺省在 artifacts/ 下找最近的 trx（同一次跑可能按项目拆成多个文件，一并取用）。"""
    artifacts = root / "artifacts"
    if not artifacts.is_dir():
        return []
    candidates = [p for p in artifacts.rglob("*.trx") if p.is_file()]
    if not candidates:
        return []
    newest = max(candidates, key=lambda p: p.stat().st_mtime)
    # 同一批（同一父目录、mtime 相差 10 分钟内）的 trx 视为同一次跑。
    latest_mtime = newest.stat().st_mtime
    return sorted(p for p in candidates if latest_mtime - p.stat().st_mtime <= 600)


def extract_tokens(text: str) -> list[tuple[int, str]]:
    """抽反引号内的用例令牌，忽略 `path.cs::Symbol` 这类改动位置引用。"""
    found: list[tuple[int, str]] = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        for raw in _TOKEN_RE.findall(line):
            token = raw.strip()
            if "/" in token or "\\" in token or token.endswith(".cs"):
                continue
            if not _NAME_RE.match(token):
                continue
            found.append((lineno, token))
    return found


def collect_trx(paths: list[Path]) -> list[Path]:
    files: list[Path] = []
    for path in paths:
        if path.is_dir():
            files.extend(sorted(p for p in path.rglob("*.trx") if p.is_file()))
        elif path.is_file():
            files.append(path)
    return files


def match_token(token: str, names: set[str]) -> tuple[list[str], bool]:
    """返回（命中的 trx 全名列表, 是否行级命中）。"""
    base, sep, rest = token.partition("(")
    params = f"({rest}" if sep else ""
    norm = base.replace("::", ".").strip()

    hits = [
        n
        for n in names
        if n == norm
        or n.endswith("." + norm)
        or n.startswith(norm + "(")
        or ("." + norm + "(") in n
    ]
    row_level = any("(" in n for n in hits)

    if params:
        # 行级令牌：优先挑参数摘要逐字对得上的那一行。
        key = re.sub(r"\s+", " ", params)
        exact = [n for n in hits if key.strip("()") and key.strip("()") in re.sub(r"\s+", " ", n)]
        if exact:
            return sorted(exact), True
        if hits:
            return sorted(hits), True
    return sorted(hits), row_level


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="ATTRIBUTION.md 用例令牌 × trx testName 轻量校验")
    parser.add_argument(
        "--trx",
        nargs="+",
        type=Path,
        default=None,
        help="trx 文件或目录（可多个）；缺省取 artifacts/ 下最近的一批 trx",
    )
    parser.add_argument(
        "--attribution",
        type=Path,
        default=DEFAULT_ATTRIBUTION,
        help="归因表路径；缺省同目录 ATTRIBUTION.md",
    )
    parser.add_argument("--quiet", action="store_true", help="只打印汇总，不逐条列出警告")
    args = parser.parse_args(argv)

    attribution = args.attribution
    if not attribution.is_file():
        print(f"[错误] 归因表不存在：{attribution}", file=sys.stderr)
        return 1

    trx_files = collect_trx(args.trx) if args.trx else find_trx(REPO_ROOT)
    if not trx_files:
        print("[错误] 找不到 trx：请用 --trx 指定，或先跑一次全量测试", file=sys.stderr)
        return 1

    names = load_trx_names(trx_files)
    text = attribution.read_text(encoding="utf-8")

    groups = re.findall(r"^###\s+(.+)$", text, re.M)
    tokens = extract_tokens(text)
    if not tokens:
        print("[错误] 归因表里没有任何 `类名::方法名` 令牌", file=sys.stderr)
        return 1

    unmatched: list[tuple[int, str]] = []
    multi: list[tuple[int, str, int]] = []
    row_hits = 0
    for lineno, token in tokens:
        hits, row_level = match_token(token, names)
        if not hits:
            unmatched.append((lineno, token))
            continue
        if row_level:
            row_hits += 1
        if len(hits) > 1:
            multi.append((lineno, token, len(hits)))

    unique = {token for _, token in tokens}
    print("ATTRIBUTION 校验")
    print("-" * 60)
    print(f"归因表  : {attribution}")
    print(f"trx     : {len(trx_files)} 个 -> {', '.join(p.name for p in trx_files)}")
    print(f"trx 用例: {len(names)}")
    print(f"分组数  : {len(groups)}")
    print(f"成员数  : {len(tokens)}（去重 {len(unique)}），其中行级命中 {row_hits}")
    print(f"警告项  : {len(multi)}")
    print(f"未匹配  : {len(unmatched)}")

    if multi and not args.quiet:
        print("\n[警告] 一个令牌命中多行（[Theory] 建议写成 `方法名(参数摘要)`）")
        for lineno, token, count in multi:
            print(f"  L{lineno}  {token}  -> {count} 行")

    if unmatched:
        print("\n[错误] 未匹配到任何 trx 用例（拼错／已删／串行）")
        for lineno, token in unmatched:
            print(f"  L{lineno}  {token}")

    exit_code = 1 if unmatched else 0
    print(f"\n退出码  : {exit_code}")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
