# Implementation Grill Plan — 判定器玩家分域

> 本轮聚焦：S1 玩家域承载形态与玩家上下文的到达路径。

## 硬约束（来自代码，不可绕过）

- `ServiceRegistry<TService,THandle,TWrapperHandle>` 是 `JudicatorRegistry` 与 `AssetServiceRegistry` 的**共用泛型基类**（`UnifiedServices.cs:240`）；`ServiceEntry` 持有三组 wrapper 列表（`:54-56`）。改分桶会同时影响资产服务。
- `JudicatorBinding` 固定 `JudicatorRegistration`，`InvokeValidation(refs, subject)` **无 player**（`JudicatorBinding.cs:58`）。
- `Trigger.BindValidation(binding, subjectProvider)` **无 player**（`Trigger.cs:372`）；`EvaluateValidation` 同样无（`:357`）。
- 卡在 `CardLibrary` 加载期绑定判定器（`Match.cs:437` → `CardLibrary.cs:131`），**此时 `card.Owner` 尚未确定**——绑定不能依赖玩家。
- `Match._judicators` 为对局级单例（`Match.cs:47/528`），`Match.Judicators` 公开属性被 `Match.CanAcceptInput`、`ResourceManager`、`EffectRuntime` 使用。

## Unresolved Questions

### I1: 玩家域的承载形态 【已定：a（单注册表 + 按玩家分桶）】

### I2: 玩家上下文如何到达判定器调用点 【已定：I2a–I2d】
- `InvokeValidation(refs, subject, int? playerIndex = null)`；`BindValidation(..., playerProvider)`；直接调用方显式传；**对外 `null`＝默认域，内部哨兵 `-1`**。

### I3: 名单判定器的签名与来源上下文 【已定：a】
- 签名 `(Card viewer, EffectSelector selector) → IReadOnlyList<Ref<Entity>>`；`effect.target.resolve` 纯转发；`self` 分支不过滤；`JudicatorSelectionRule` 改为名单成员判定。

### I4: DSL/卡数据的声明载体 【已定：a + b】
- `KeywordRulePatch` 加 `Source` 字段；`RuleNames` 加 `targeting.forbid`；`Scope` 保持保护范围语义（"敌方"方向运行时判定）；动态走 `Operations`；禁止合取。
- 用户补充：**生命周期用"效果包裹注册与注销"**（不把注册/注销散落在组件里）。

### I5: 声明的生命周期载体 【已定：a】
- 独立声明类型持有句柄；`ModifierExpiry` 复用；来源标记＝效果实例；离场兜底＝订阅离场 + wrapper 求值校验在场。

### I7: `includeCovert` 请求级放宽的形态 【已定：a】
- `EffectSelector` 增加 `bool IncludeCovert = false`（位置记录 + 默认值，源码向后兼容）；剔除逻辑单点在名单判定器内。
- 兼容口径：**单向**——新引擎可读旧数据；旧引擎读新数据（带 `includeCovert` 字段）不保证。
- 形态保持**请求级**（运行时传 selector），不进卡效果静态声明。

### I6: 测试与清单迁移顺序 【In Progress】
- 受影响测试：`JudicatorTargetSelectionTests`、`CovertMechanismTests`、`JudicatorAssemblyTests`（22→23 条）、`JudicatorBindingTests`、`JudicatorValidationTests`、`JudicatorRevalidationDedupTests`、`JudicatorCombatLegalityTests`、`JudicatorCombatCounterAmbushTests`、`LegacyTargetingShims`。
- a) 串行 S1→S5，每步保持可编译可绿（推荐）。
- b) 机制与名单判定器并行改造。
- c) 先改测试（TDD）。
- 子问 I6a：`JudicatorSelectionRule` 旧构造是否保留（无 viewer 时退化）？推荐不保留，直接改测试（旧构造无法产名单）。
- 子问 I6b：`JudicatorWrapperTests` 是否补"按玩家分桶"用例（新机制）？推荐补。

### I3: 名单判定器的签名与载荷 【Pending】
- 签名：`(Player viewer, EffectSelector selector) → IReadOnlyList<Ref<Entity>>`？
- 是否仍保留 `JudicatorSelectionRule.IsEligible(candidate)`（单项复核用）？
- `EffectTargetResolveJudicator` 如何复用（输出 `Card` vs `Ref<Entity>` 的转换）？

### I4: DSL 声明路径 【Pending】
- `KeywordProgram.RuleNames` 是白名单（10 条，`KeywordProgram.cs:34`）；`KeywordRulePatch.Scope` 白名单为 `self/linked/pair/friendly/enemy/all`（`:29`），与 D22 的 `opponent/friendly/any` 不一致。
- 新增规则名（如 `targeting.candidate.forbid`）还是新增 `Operations`（如 `targeting.forbid`）？
- 静态卡数据字段放哪（`CardDefinition` / `TagData` / 新组件）？

### I5: 运行时挂载/卸载的锚点 【Pending】
- 谁持有 `JudicatorWrapperRegistration` 句柄（卡组件？`ProgramKeywordComponent`？）？
- 卡离场/死亡/被沉默的钩子在哪（`CardLifecycle` / `ProgramKeywordComponent`）？
- 是否需要"对局级收集面"（类似 `AuraSystem`）统一撤销？

### I6: 测试与清单迁移顺序 【Pending】
- 先改机制（S1）再改名单判定器（S2），还是并行？
- 哪些测试会红（`JudicatorTargetSelectionTests`、`CovertMechanismTests`、`LegacyTargetingShims`）？
