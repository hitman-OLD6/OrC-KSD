# Implementation Grill Plan — 战术词条卡包（机动 / 固守 / 抢修）

> 需求已对齐（包名占位）；本轮聚焦实现路径与代码冲突。

## Unresolved Questions

### Q1: 行动额度组件的落点 Completed（C）
- 结论：`CommandData` 内加 `MoveQuota`/`AttackQuota`（`internal set`），`CanMove`/`CanAttack` 改派生只读；读取点零改动，写入点集中迁移。

### Q2: 机动的拦截点 Completed（A）
- 结论：新增"行动收尾"对局级具名触发器（`action.quota`，登记进 `MatchNamedTriggers`）；`OperateFinalizer` 先填默认额度消耗 → 触发 → 应用结果；机动效果经 `injects` 注入 handler（攻击分量 +1、移除自身）。
- 依据：卡包词条为纯标记；`injects` 支持对局级触发器回退；触发器支持前置/后置改写；时序在写额度之前。

### Q3: 固守的指挥移动读口 Completed（A：judicatorWrappers）
- 用户否决"引擎读词条字面"（失败模式）；改为效果自注册包装器。
- 结论：预制体新增 `judicatorWrappers[]`（`{ judicator, phase, handler, seam? }`）；装载时与 `injects` 对称落地（解析判定器 → 强类型适配 → 注册 → 随效果卸载注销）。
- 固守：后置包装器挂 `move.leg.eligibility`（结果通过且单位==宿主 → 返回失败）。

### Q4: 抑制资格判定器 Completed（A + a）
- 结论：新增 `inhibit.leg.eligibility`（结构化结果 `InhibitEligibilityFailure?`：`Dead`/`Immune`）；默认只判死亡（`被抑制` 是纯视觉标记，可重复抑制）。
- `无法被抑制` 改为自注册包装器（新增内建 prefab + handler）；`InhibitRules` 只调判定器。
- 压制侧 `无法被压制` 本轮不迁移（保持现状，待确认口径）。

### Q4c: judicatorWrappers 的 handler 载体 Completed（B：csx 同步）
- 结论：`judicatorWrappers[]` 内联 csx；需新增**同步**脚本求值契约（产出 `Func<object[]?, object[]?, object[]?>`），由 `Orc.Script` 实现（加性，不改既有 `IScriptEvaluator`）。
- 装载落地与 `injects` 对称；随效果卸载注销。

### Q5: 抢修记录与触发 Completed（A：通用回合统计组件）
- 结论：单位级「本回合统计」组件 `ActionRecordData`：`TurnActions`/`TurnMoves`/`TurnAttacks`/`TurnPointsSpent`/`TurnDamageTaken`（不含修复量）；每己方 `turn.start.after` 清零。
- 抢修条件读 `TurnMoves == 0 && TurnAttacks == 0`。
- 维护：引擎侧单一订阅器 `UnitTurnStatsTracker`（A）。

### Q5b: 统计组件载体 Completed（A）
- 结论：单一订阅器维护，数据组件保持纯数据；抢修在 `turn.start` 读、清零在 `turn.start.after`。

### Q6: 卡包目录与制品 Completed（A + C）
- 落点：`samples/card-pack-bulwark/`；`manifest.id="orc.bulwark"`、`kind=cardpack`、固定 guid、`version=1.0.0`；README 标题「铜墙铁壁系列」。
- 制品：`keywords.json`（机动/固守/抢修）＋ `effects/` 三个 csx prefab ＋ `cards/` 三张示例卡（各带一个词条）。
- 内建 `无法被抑制` 的 prefab 属引擎侧（`KeywordPrefabs/keyword.cannot-be-inhibited.prefab.json`），不入卡包。

### Q7: 测试范围与基线 Completed（A）
- 全量基线 `dotnet test OrcEngine.sln` 必须全绿；额度模型改变的旧断言一并更新。
- 新增六组测试：额度模型 / 机动 / 固守 / 抢修 / 统计组件 / 卡包端到端。

### Q3: 固守的指挥移动读口 Pending
- 卡包词条是纯标记、不贡献 `move.allowed` 规则；候选：`LegEligibilityJudicator` 加读口 / 新判定器 / 引擎内建程序化词条。

### Q4: 抑制资格判定器 Pending
- 判定器名（受控常量）、注册域、与 `CannotBeInhibited` 标记并存/迁移、卡包词条如何返回"否"。

### Q5: 抢修记录与触发 Pending
- 移动/攻击记录落在哪（词条组件自维护 vs 单位数据）；`turn.start` 与行动位恢复的顺序；前线失去词条的触发点（位置变更/部署/效果位移）。

### Q6: 卡包承载 Pending
- 包目录位置、`keywords.json` 绑定 prefab、行为制品用 csx 还是 C#（固守所需的引擎读口不在卡包能力内）。

### Q7: 测试策略 Pending
- 额度模型矩阵、词条行为、卡包端到端的测试落点与基线更新。
