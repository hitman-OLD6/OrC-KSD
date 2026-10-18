# Implementation — 判定器玩家分域

> 实现 grill 已完成；S1–S5 已落地并通过全量测试（1612 项，0 失败）。已知缺口见文末。

## Scope

- Target：`src/Orc/Core`（判定器机制）、`src/Orc.Game/Judicators`、`src/Orc.Game/Targeting`、`src/Orc.Game/Managers`、`src/Orc.Game/Commanding`、`src/Orc.Game/Cards`、`src/Orc.Plugins.BuiltIn`、`src/Orc.Packages`、`tests`
- Excludes：UI 投影、AI 策略、数值修饰链、联网重连的状态重建

## Implementation Steps

### S1: 玩家域承载与解析入口
- Status: Done
- Target：`src/Orc/Core/UnifiedServices.cs`、`JudicatorRegistry.cs`、`JudicatorBinding.cs`、`Trigger.cs`、`Match.cs`
- Approach：单注册表 + 条目内 wrapper 栈按玩家分桶；对外 `int? playerIndex`（`null`＝默认域），内部哨兵 `-1`。
  - `ServiceEntry`：`Dictionary<int, WrapperBucket>`；`Invoke(args)`（默认域）/ `Invoke(args, domain)`。
  - **两层管道**：全局层（默认域，插件/装配期注册）→ 玩家层（卡效果注入）；Replace 玩家层优先、否则全局层；After 玩家层先、全局层后。
  - `JudicatorBinding.InvokeValidation(refs, subject, playerIndex = null)`。
  - `Trigger.BindValidation(binding, subjectProvider, playerProvider)`；卡侧传 `_ => _card.Owner?.Index`，移动/攻击复验从 `refs[0]` 取 Owner。
  - `Match.CanAcceptInput` 传 `player.Index`。
- Acceptance:
  - [x] 同一判定器条目可按玩家持有独立 wrapper 栈，互不影响（`JudicatorWrapperTests` 新增 2 例）
  - [x] 无 player 的调用走默认域，行为等同改造前（全量回归绿）
  - [x] 卡加载期绑定不依赖 Owner
  - [x] 既有 `Invoke(args)` 调用点无需修改即可编译
  - [x] 全局层 wrapper 对玩家域调用仍生效（修复 `JudicatorValidationTests` 回归）

### S2: 名单判定器改造
- Status: Done
- Target：`TargetEligibilityJudicator.cs`、`EffectTargetResolveJudicator.cs`、`JudicatorSelectionRule.cs`、`EffectRuntime.cs`（`EffectSelector`）、`BuiltInPlugin.cs`
- Approach：
  - `TargetEligibilityJudicator` → `(Card viewer, EffectSelector selector) → IReadOnlyList<Ref<Entity>>`；默认规则抽为 `TargetCandidateResolver`（战场枚举 + side/zone/type/keyword/threshold + 隐蔽剔除，`IncludeCovert` 请求级放宽）。
  - `EffectTargetResolveJudicator` 转发名单判定器（单真源），只负责 `sel/count/random` 取样。
  - `JudicatorSelectionRule` 语义＝名单成员判定（`Candidates` / `IsEligible` / `AsPredicate` / `Filter`）。
- Acceptance:
  - [x] 判定器输入＝（来源卡视角，`EffectSelector`），输出＝候选名单
  - [x] 交互索敌与无头选靶同源（`effect.target.resolve` 转发）
  - [x] 提交侧复核保留
  - [x] `sel == "self"` 不受可选性限制
  - [x] HQ 候选行为与改造前一致
  - [x] 隐蔽剔除与 `IncludeCovert` 生效（23 项相关测试绿）

### S3: 可选性声明与包装
- Status: Done（静态声明 + 运行期载体；DSL 动态动作未实现）
- Target：`KeywordProgram.cs`、`ProgramKeywordComponent.cs`、`TargetingDeclaration.cs`、`KeywordProgramRuntime`
- Approach：
  - `KeywordRulePatch` 新增 `Source`（`any`/`command`/`unit-effect`）；`RuleNames` 新增 `targeting.forbid`；`Validate` 校验 `Source`。
  - 新增 `TargetingDeclaration`（public）：持有 `JudicatorWrapperRegistration`；`Attach(对手域)` 注册 `After`（剔除被保护目标）；`Detach` 注销；方向＝归属对立，作用域＝`protects` 谓词（复用组件的 `Applies`）。
  - `ProgramKeywordComponent.Mount` 解析 `targeting.forbid` 并 `Attach` 对手域；`Unmount` 全部 `Detach`。
  - 离场兜底：wrapper 求值时经 `isActive`（`OnBoard`）校验来源仍在场。
- Acceptance:
  - [x] 词条静态声明可产出限制
  - [x] 多条限制经 `After` FIFO 合取（全局层 + 玩家层）
  - [x] 来源离场/卸载自动 `Detach`（`Unmount` 链）
  - [x] 旧词条数据（无 `Source`）解析行为不变
  - [x] 运行期动态赋予/剥夺（DSL `targeting.forbid`/`allow` 动作）
  - [x] 卡面文本/DSL 直接表达（经词条程序 Actions）

### S4: 回合准入下沉
- Status: Done
- Target：新增 `ActionTurnOwnerJudicator.cs`；`LegEligibilityJudicator.cs`、`PlayManager.cs`、`CounterUseJudicator.cs`、`BuiltInPlugin.cs`、`Match.cs`
- Approach：
  - `action.turn.owner`：载荷 `[player]` → bool。
  - `BuiltInPlugin` 注册并构造 `TurnOwner` 通道；`LegEligibilityJudicator`（move/attack）、`CounterUseJudicator` 注入。
  - `PlayManager` 新增 `turnOwner` 可选参数，`CheckAdmission` 走判定器。
  - `CounterUseJudicator`：先判回合，再以 owner 传给 `CounterCard.EvaluateUse`（保留费用检查）。
- Acceptance:
  - [x] 三处入口同源
  - [x] 改造前后行为逐字一致（全量回归绿）
  - [x] `GameHooks` / `GameHooksJson` 清单同步（23 条）

### S5: 兼容与测试迁移
- Status: Done
- Target：`GameHooks.cs`、`GameHooksJson.cs`、测试
- Approach：`JudicatorNames` 加 `action.turn.owner`（23 条）；`GameHooks.JudicatorNameList` / `BuiltInJudicatorNames` / `GameHooksJson.JudicatorMetadata` 同步；`JudicatorTargetSelectionTests` / `CovertMechanismTests` 迁移到新签名与域参数；`GameHooksTests` 数量断言 22→23、21→22。
- Acceptance:
  - [x] 23 条判定器名与文档一致
  - [x] 无 player 重载保留，旧测试语义不变（1612 项全绿）
  - [x] `GameHooks` / `GameHooksJson` 与代码一致

## 缺口补全记录（第二轮）

1. **交互式索敌统一入口**：已补 `EffectRuntime.BeginTargetSelectionAsync(Card viewer, EffectSelector, ct)`——候选取自名单判定器、玩家域＝viewer 归属域、提交侧用名单快照复核；测试 `BeginTargetSelection_Excludes_Protected_Target_Through_Opponent_Domain`。
2. **DSL 动态动作**：`KeywordProgram.Operations` 新增 `targeting.forbid` / `targeting.allow`；`ProgramKeywordComponent.Execute` 经 `ApplyTargetingDeclaration` 创建/撤销声明（来源＝宿主、保护目标＝动作 target、来源类型＝`a.Key`）；测试 `Dsl_TargetingForbid_Removes_Protected_Unit_From_Opponent_Domain`。
3. **玩家域访问控制**：仍未做运行时强制。D30 的"不可读他人域"由 API 面保证（未提供跨域读接口），不引入运行时校验；如需强制，需在 `JudicatorRegistry` 增加"调用者域句柄"概念（未做）。
4. **名单重算**：已补 `JudicatorSelectionRule.Snapshot()`（单次请求内名单只求一次）；交互式入口已改用快照复核。`AsPredicate`/`AsFilter` 保持实时语义（跨多次交互不能缓存）。
5. **schema 文档**：`KeywordRulePatch.Source` 已在代码 XML 注释中说明；`RuleNames`/`Operations` 白名单以代码常量为唯一来源（无独立 schema 文档）。

## 仍存在的约束（非缺口）

- 同一条目多层包装器的执行顺序：玩家层先于全局层（After）、全局层先于玩家层（Before）；各限制必须为合取且与顺序无关（D31）。
- `TargetingDeclaration` 当前只支持"保护单个目标"（`protects` 谓词由调用方给出；静态词条声明复用组件 `Applies` 支持 `self/friendly/linked/pair` 等范围）。

