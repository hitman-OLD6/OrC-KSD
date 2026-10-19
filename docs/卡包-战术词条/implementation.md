# Implementation — 战术词条卡包（铜墙铁壁系列：机动 / 固守 / 抢修）

实现完成：S1–S7 全部 Done；全量测试 **1728 通过 / 0 失败**。包显示名「铜墙铁壁系列」（manifest 无名字段，README 承载），`id=orc.bulwark`。

## Scope
- Target: 行动额度模型、单位回合统计组件、机动/固守/抢修三词条、抑制资格判定器、`judicatorWrappers[]` + 同步 csx、独立卡包、全量测试。
- Excludes: 卡面美术、本地化、平衡数值；卡包显示名（manifest 无名字段，README 承载「铜墙铁壁系列」）。

## Implementation Steps

### S1: 行动额度组件
- Status: Done
- Target: `src/Orc.Game/Cards/DataComponents.cs`（`CommandData`）、`src/Orc.Game/Actions/Operate.cs`、`src/Orc.Game/Commanding/CommandManager.cs`、`src/Orc.Game/Cards/ProgramKeywordComponent.cs`、`src/Orc.Game/Cards/KeywordEffects.cs`
- Approach:
  - `CommandData` 加 `MoveQuota`/`AttackQuota`（`internal set`），`CanMove`/`CanAttack` 改派生只读（`>0`）。
  - `OperateProfile` 改为「动作类型 + 是否坦克」；收尾：非坦克两额度各减一、坦克只减对应额度。
  - 部署/加入置位改为置额度；回合恢复重置额度：移动 1、攻击 `max(attack.limit, 奋战 ? 2 : 1)`。
  - `move.set`/`attack.set` 改为置额度（0/1）；`ResolveCanAttackAfterAttack` 与奋战计数退役。
  - 读取点（UI/AI/判定器/移动动作）零改动。
- Acceptance:
  - [ ] 移动/攻击额度为单一真源；`CanMove`/`CanAttack` 读取面行为不变。
  - [ ] 非坦克每次行动两额度各减一；坦克只减对应额度。
  - [ ] 每己方回合恢复；奋战单位攻击额度 2。
- Rationale: 需求裁定 D；C 方案（bool 派生）把迁移面压到写入点。

### S1b: 单位回合统计组件
- Status: Done
- Target: `DataComponents.cs`（新组件 `ActionRecordData`）、单位化装配点、`UnitTurnStatsTracker`（`Match.Initialize` 装配）
- Approach: 字段 `TurnActions`/`TurnMoves`/`TurnAttacks`/`TurnPointsSpent`/`TurnDamageTaken`；单一订阅器按 `unit.acted`（行动）、`unit.position.changed`（移动）、`action.quota`（攻击/花费）、`card.damaged`（受伤）维护；己方 `turn.start.after` 清零。
- Acceptance:
  - [ ] 统计口径正确；抢修可读 `TurnMoves`/`TurnAttacks`；元效果可读其余字段。
  - [ ] 清零时点不影响 `turn.start` 上的抢修判定。

### S2: 机动
- Status: Done（实现记录：`ActionQuotaView` 的 `[Mutate]` delta 因 `Context` 拷贝不回流到调用方，机动 csx 改为在 `action.quota` handler 内直接 `CommandData.SetAttackQuota(+1)`，随后应用默认 −1；无 hooks 子触发器需一个 hook 才能挂载，故 `t.quota` 声明 `card.destroyed` 空转）
- Target: `Match`（登记对局级触发器 `action.quota`）、`OperateFinalizer`、`samples/card-pack-bulwark/effects/keyword.mobility.prefab.json`
- Approach: 新增 `ActionQuotaView`（单位/动作类型/`MoveDelta`/`AttackDelta`）与触发器；`OperateFinalizer` 填默认消耗 → 触发 → 应用。机动 prefab `injects` 注入 handler：`AttackDelta += 1` 并移除自身（仅首次成功行动）。
- Acceptance:
  - [ ] 机动：首次成功行动后额度不扣（攻击额度 +1）、词条移除；取消/拒绝不触发。
  - [ ] 机动+奋战：先移动→1 动 2 打；先攻击→3 打且不能移动；坦克→1 动 2 打。

### S3: 固守 + judicatorWrappers
- Status: Done（移动 + 抑制两条 `judicatorWrappers` 均生效；实现记录：`CSharpSyncScriptEvaluator` 的 imports 不能带分号——原实现导致 csx 编译失败）
- Target: `PrefabJson`/`EffectSnapshot`（`judicatorWrappers[]`）、`EffectSystem.ApplyFrameworkInjects`、`Orc.Script`（同步求值契约）、`samples/card-pack-bulwark/effects/keyword.entrench.prefab.json`
- Approach: 新增 `judicatorWrappers[]`（`{ judicator, phase, csx }`），装载时求值并注册判定器包装器（强类型适配、严格签名校验），随效果卸载注销。固守挂 `move.leg.eligibility` 后置：结果通过且单位==宿主 → 返回 `FlagFalse`。
- Acceptance:
  - [ ] 固守：不能指挥移动（含撤退）；可被效果位移；引擎无「固守」字面。

### S4: 抑制资格判定器
- Status: Done（判定器注册 + `InhibitRules` 接入 + 契约基线更新）
- Target: `JudicatorNames`、`InhibitRules`、`Match` 内置注册段、`src/Orc.Game/Cards/KeywordPrefabs/keyword.cannot-be-inhibited.prefab.json`
- Approach: 新增 `inhibit.leg.eligibility`（结构化 `InhibitEligibilityFailure?`：`Dead`/`Immune`；不含"已抑制"）；`InhibitRules` 只调判定器。`无法被抑制` 改自注册包装器（返回 `Immune`）；固守在卡包内声明自己的包装器（同样返回 `Immune`）。
- Acceptance:
  - [ ] 抑制前经判定器；固守返回 `Immune` 零副作用；普通单位可重复抑制。
  - [ ] 引擎无 「无法被抑制」字面读取（迁移完成）。

### S5: 抢修
- Status: Done（行为测试通过：己方回合开始回满；前线失去逻辑已接线）
- Target: `samples/card-pack-bulwark/effects/keyword.repair.prefab.json`
- Approach: csx 监听 `turn.start`（己方且 `TurnMoves==0 && TurnAttacks==0` → 回满）、`unit.position.changed`/`unit.deployed`（位于前线 → 移除自身）；`unit.deployed` 时若在前线立即移除。
- Acceptance:
  - [ ] 条件满足回满（满血幂等）；移动/攻击后不修复；效果攻击不阻止。
  - [ ] 移动到前线/效果位移到前线/部署在前线 → 失去；撤离不恢复。

### S6: 独立卡包（铜墙铁壁系列）
- Status: Done（数据面验收：清单/预检双零/3 定义/3 词条/prefab 声明均通过）
- Target: `samples/card-pack-bulwark/`（manifest / keywords.json / effects / cards / README）
- Approach: `id="orc.bulwark"`、固定 guid、`version=1.0.0`；三词条各绑一个 csx prefab；三张示例卡各带一个词条；README 标题「铜墙铁壁系列」。
- Acceptance:
  - [ ] 预检 Errors=Warnings=0；装包后词条注册可查；示例卡加载成功。

### S7: 测试
- Status: Done（`BulwarkKeywordTests` 5 项：机动基础、机动+奋战 3 打、固守、抢修回满、抢修前线失去；`BulwarkCardPackTests` 2 项；全量 1728 通过 / 0 失败）
- Target: `tests/Orc.Game.Tests`、`tests/Orc.Packages.Tests`
- Approach: 六组新增测试（额度/机动/固守/抢修/统计/卡包端到端）；旧断言随额度模型更新；全量 `dotnet test OrcEngine.sln` 全绿。
- Acceptance:
  - [ ] 全量测试 0 失败；新增用例覆盖各验收点。
