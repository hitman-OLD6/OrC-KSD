# 执行进度 — 人类 CLI 客户端

- 更新时间: 2026-10-22T21:58:00+08:00
- 工具调用计数: ~86
- 当前步骤: 全部完成（S1–S13）
- 总体状态: 已完成

## 已完成
- S1 传输层：`transport.py`（读线程 + 响应 Future + 待决条件变量 + 推送队列 + 写锁 + 心跳；超时/重试白名单按 D11）。
- S2 管理器面：`manager.py`（ping 探活、detached 拉起、三项 preflight、5MB 日志截断、spawn/admin/kill、绝不 kill 管理器）。
- S3 新开实例：`cli.py::_boot_play`（spawn → attach → conn.admit → match.start，失败清理并打印实例标识）。
- S4 Session：`session.py`（单写仓库 + 快照读、按 roomId 分段日志、轮换清理、unknown 自动清除）。
- S5 指令集：`commands.py`（D14 全 16 条 + 中文别名、内联参数、引导、相位门禁、面板编号、D16 候选）。
- S6 选择模式：序号多选 / ok / x，`formation-gap`→「插入位置 k：…」，`target.cancel` 分支。
- S7 叙述层：`render.narrate_segment`（D8.1 过滤、D9.1 模板、四条折叠 + 同段重复合并、D8.4 降级、D9.5 追溯）。
- S8 渲染：`render.py` 两套布局（play 视角 / watch 中性）、`front_plain.py`、`front_tui.py`、CJK 宽度自算。
- S9 判定链：回座 / 同实例重开 / 新开三条出口；`restart` 四步 + deck-invalid 重生成重试一次。
- S10 观战：`_boot_watch`（无座 viewer → omniscient）、白名单、中性标题、单侧退化标注。
- S11 卡包卡组：manifest.metadata 或递归扫描、`pack/<包id>/<资产id>`、39 张循环、`--deck` 覆盖、写回记录。
- S12 退出清理：`Runtime.shutdown` 单一出口（kill/保留 → 记录去留 → 关 socket）。
- S13 可测试性：`IDispatcher` + `tests/fake.py::FakeDispatcher`，**101 条 unittest 全绿**。

## 端到端验证（管理器 127.0.0.1:52001，--launch-manager）
- 新开：`printf 'keep\nquit\n' | python tools/orc_play.py play --plain --manager 127.0.0.1:52001 --launch-manager`
  ⇒ 管理器由本会话拉起并常驻；spawn → attach → admit → match.start；keep 确认；quit 二次确认；实例销毁；记录删除；EXIT=0。
- 保留 + 回座：`--keep-instance` 退出后记录留存；再次 `play` 输出「已凭座位令牌回到实例 …」，直接进入对局（跳过 spawn/admit/start）。
- 出牌两步：`play 1` → 「选择部署位置（选 1..1）」+「插入位置 1：最左 / 插入位置 2：最右」；`1` → 已选 1；`ok` → 「已提交选择」+ 叙述「你：打出 AI 样例火炮 并部署到战场」；棋盘出现「1. AI 样例火炮 支援线第2槽 2/2」。
- 观战：`watch --instance <id> --plain` 显示 A/B 双方手牌与共享前线；`play 1` 被本地拒绝「观战模式只读…」；quit 直接退出；记录与对局状态均未变。

## 父 agent 复核与微调（执行完成后）
- 复核方式：重跑 `unittest`（101 全绿）＋ `play --plain`（新开/出牌两步/quit 二次确认）＋ `play --keep-instance` → `watch --plain`（双方手牌 / units / 空白单位态 / 只读拒绝）→ `manager.kill` 清理。
- 落地 6 处体验修正（见 implementation.md 的 D17.1–D17.6）：手牌按位置编号不重排、装配类 log/update 默认静音（`--verbose` 可见）、脱敏行去 `×N`、plain 块内不重复日志、`需选 N 个` 文案、watch 空态按座位。
- 清理：`.orc-manager/cli/session.json` 已删除（测试记录指向已销毁实例）；`outputs/cli-probe/deck-infantry.json` 保留（39 张 1 费卡组，便于复现）。

## 未开始 / 未自动验证
- TUI 真实终端交互（无 TTY 环境）：已做无头冒烟（导入、布局文本、提示条、补全、Tab 不执行），未做人工按键冒烟。

## 阻塞 / 风险
- 本沙盒每次 Bash 调用结束会回收其子进程树，因此"CLI 拉起的管理器在跨调用后仍存活"只能在**同一次调用内**验证（已验证：ping=True、实例清零）。真实终端下 detached 进程不受此限。
- 启动段（match.start 之后第一段）含数十条装载噪音（`已挂载触发器`/`card.load`），按 D8.1/D8.4「不透传即丢失」的裁定保留；同段内连续相同行已压成 `×N`。
