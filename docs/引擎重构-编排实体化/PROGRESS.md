# 执行进度 — 编排实体化（批次 9：`Emit` 不执行订阅者的根因修复 ＋ §28.5 第 4–7 项补齐）

- 更新时间: 2026-10-28（本轮）
- 当前步骤: **§三十一**（最新一节）——`Orc.Game.Tests` 最后 13 条红的夹具收尾：`--filter`
  `Command|Play|Counter|Battle|Damage|Order` 家族 **270 条 0 红**（见 §31）。
- 总体状态: `bash artifacts/build-src.sh OrcEngine.sln` **0 错误**；`Orc.Tests` **324/0**；
  `Command|Play|Counter|Battle|Damage|Order` 子集 **270/0**。
- 阅读顺序建议：本轮只看 §三十一；§二十九／§三十 是上一轮的生产侧修复记录。

**U0 全量回归（`dotnet test D:/OrC-KSD/OrcEngine.sln --artifacts-path artifacts/test-s13-u0`）：
2547 通过 / 0 失败 / 0 跳过**（Orc.Game 1405、Orc.Tests 471〔+3〕、Orc.Packages 369、Orc.Mcp 118、
Orc.Server 112、Orc.Script 39、Orc.Manager 20、Orc.Ai 13）。基线 2544 → **+3 条新测试**（`OwnEdgePositionTests`）；**未删除任何测试**。

**U1 全量回归（`--artifacts-path artifacts/test-s13-u1`）：2548 通过 / 0 失败 / 0 跳过**（Orc.Game 1405→1406，+1 守护测试）。
**U2 全量回归（`--artifacts-path artifacts/test-s13-u2`）：2549 通过 / 0 失败 / 0 跳过**（Orc.Game 1406→1407，+1 地址登记测试）。
三个 U 步累计 **+5 条测试、0 删除、0 改写为"弱断言"**；`new Trigger<` 全程 38 处／17 文件（**未动旧实例化点**）。

**批次 5 基线（`--artifacts-path artifacts/test-batch5-full2`）：2544 通过 / 0 失败 / 0 跳过**
（Orc.Game 1405、Orc.Tests 468、Orc.Packages 369、Orc.Mcp 118、Orc.Server 112、Orc.Script 39、Orc.Manager 20、Orc.Ai 13）。

### 批次 6（本轮）执行进度

| 步 | 内容 | 状态 | 证据 |
| --- | --- | --- | --- |
| U0 | `MemberDefinition.OwnEdgePosition`（默认 First）＋ `before` 相位 LIFO→FIFO | ✅ | 2547/0/0；新增 `tests/Orc.Tests/Model/OwnEdgePositionTests.cs`（3 条）；改写 4 条依赖 LIFO 的断言（见 §A1.1） |
| U1 | `EngineProcesses` 补 13 个成员名＋管道型标 `last` | ✅ | 2548/0/0；`EngineEntityTests.ActionPipelineMembers_AreDeclared_WithOwnEdgePositionByKind`（新增）；`EngineMemberConstants_MatchPrefabMembers` 9→22（见 §A1.2） |
| U2 | `TriggerRegistry` 按成员地址登记／`ActionTriggerEnd` 承载成员句柄／`End<TView>` 去泛型 | ✅ | 2549/0/0；`TriggerRegistryTests` 三处断言加宽＋`AddressOnly_Registration_IsAddressable_WithoutLegacyIdentity`（新增）；未触碰 `I*Action`（见 §A1.3） |
| U3 | 契约加宽：6 个 `I*Action` 属性 → 成员句柄；`DamagePipeline`／`EffectRuntime.Board` 同批 | ✅ | **2570/0/0**（+21 条）；逐动作全绿 2552→2555→2558→2560→2564→2567→2570；见 §A2 |
| U4 | 21 端 `own-edge-position` 落表（21/21 last）＋ D2 改声明序 ＋ 7 组 × 3 条等价测试 | ✅ | **2591/0/0**（+21 条）；逐组全绿 2573→2576→2579→2582→2585→2588→2591；见 §A3 |
| U5-a | band 消费者下线（src/tests 双零）＋ `CardPackAssembler` 选 effects-v4 ＋ 卡牌↔实体桥 | ✅ | **2602/0/0**（+11 条）；见 §A4 |

---

## 一、本批完成项（逐条对照）

| # | 任务 | 状态 | 证据 |
| --- | --- | --- | --- |
| 1 | 生产装载切到 effects-v4 + 卡牌↔实体桥 | ✅ **U5-a 完成**（§A4.4／§A4.5）：`CardPackAssembler` 对带 `effects-v4` 的包只装 v4；卡加载建实体、`Bound` 绑卡、组件按声明序装配、销毁撤销。**遗**：信号投递仍走旧总线（A2） |
| 2 | S13 旧类型一次性退役 | ◐ **U5-b 部分**（§十五） | 已删：`ActionTriggerBands`／`ActionHandleBands`（类型，src+tests 0）、`TriggerEventInfo.BandValue`（公开面）、审查 `events[].band`。**未删**：`new Trigger<` 仍 **38 处/17 文件**（tests 174 处/34 文件）、`Bus` 40 处——阻塞于 A2／A3（§15.4） |
| 3 | 判定器包装执行接线（S9 遗留） | ✅ | `tests/Orc.Tests/JudgeWrapExecutionTests.cs`（6 条）：after 改写结果、before LIFO／after FIFO、replace 最后声明者、声明实体宿主回填、内联包装器不补位、未接入时行为不变；既有 `BulwarkKeywordTests`/`Judicator*` 64 条全绿 |
| 4 | S12 Protocol + targeter 提交通道 | ✅ | `EffectRuntime.SelectAsync` 三分派（确定／随机／裁定）+ `BeginTargetSelectionAsync` 删除；`tests/Orc.Game.Tests/EffectSelectorChannelTests.cs`（6 条，含审查面随机选择器）＋ `JudicatorTargetSelectionTests`（改走唯一入口） |
| 5 | S15 作者文档术语同步 | ✅ | 新增 `docs/卡牌DIY开发文档/09-新编排模型与术语迁移.md`；01/02/03/04/07/08 顶部术语提醒；04 章改为唯一入口口径；`reference/信号表` 加 `engine#<信号>` 说明；README 索引/速查/约定更新 |
| 6 | 审查面「本对局用到的随机选择器」（R2c／R7） | ✅ | `LogicEngine.RandomSelections`＋`AuditChain.RandomSelectors`＋JSON `randomSelectors[]`／文本段；`EffectSelectorChannelTests.引擎随机_审查面列出本对局用到的随机选择器`。限制：登记到"哪张卡用了什么选择器"，未与边图（哪个成员）关联 |
| 7 | S14 收口（CollectLegacy* 清理 + 前端读 packages） | ⚠️ **部分** | 前端契约已写入 `docs/前端适配-后端变更-20261020.md` §10（`packages[]` + 选择器变化）；`AuditGraphBuilder.CollectLegacy*` **未删**——它投影的正是仍然存活的 `Bus` 订阅面，随 §A 一并退役 |

### 1.1 判定器包装执行接线（S9）——做了什么

- `OrchestrationEntity.Bound`（新）：实体承载的对象（卡牌实例）。**这是卡牌↔实体桥的第一块拼板**：
  判定器包装器的"哪张卡注册了我"回填靠它（旧 `judicatorWrappers` 的载荷末位约定）。
- `JudicatorRegistry.ModelWraps`（新）：把编排运行时的 `WrapRegistry` 接到判定器调用点。
  `Match` 在建立判定器注册表后一行接入。
- `ServiceEntry.Invoke`（改）：在旧域桶管道之外执行新模型的 `judicator:<名>` 包装器，**相位语义与成员包装器一致**
  （`before` LIFO／`after` FIFO／`replace` 最后声明者）；载荷末位追加声明实体（或其宿主）的 `Bound` 对象；
  无声明实体时不补位（内联包装器自带宿主绑定）。
- `WrapRegistry.RegisterJudge`（新）：内联判定器包装器登记入口（无 handler 资产），供运行期注入与测试。
- `EffectSystem.ApplyJudicatorWrappers`（改）：有编排运行时时登记到 `WrapRegistry`（**与成员包装器同一注册面**，R1i）；
  无编排运行时的独立构造路径仍落旧域桶（同一相位语义、同一执行点）。

### 1.2 targeter／选择器（S12 + S9 融合）——做了什么

`EffectRuntime.SelectAsync` 成为**唯一选择入口**，按 `sel` 三分派：

| `sel` | 路径 | 无前端时 |
| --- | --- | --- |
| `all`／`self` | 判定器（候选集即答案，不产生 targeter） | 正常工作 |
| `random` | 引擎随机服务（`MatchRandomService`） | 生产对局**明确抛错**（新增 `MatchOptions.AllowRandomTargeting`，缺省 false，A-5） |
| `one`／`any`／其它 | **提交通道**（`TargeterManager` → 前端桥接，沿用 `sessionId`+`picks` 协议形态） | **明确抛错**（`BridgeNotAssembled`），不静默取首个 |

- `EffectRuntime.BeginTargetSelectionAsync` **删除**（零调用的死代码）；其实现体并入 `SubmitAsync`，成为 `SelectAsync` 的裁定分支。
- `EffectTargetResolveJudicator`（改）：只承担**取样**；`one/any/choose` 一律明确拒绝（判定器不代选），
  `random` 无随机服务＝明确失败（不再"取首个"兜底）。
- 沙盒（`McpService.SandboxMatch`）与沙盒式 `MatchLaunch` 装配路径显式开启随机；`play` 路径不开启。
- 协议侧**无需新增字段**：待决请求 id 已是 `sessionId`、提交结果已由 `targeter.submit` 返回、动作类别已有 `action=effect`；
  本批把"效果发起的选择"真正接到这条通道上（原先效果路径绕过它）。

## 二、代码清单（本批新增／修改）

**内核**
- `src/Orc/Core/Model/OrchestrationEntity.cs` — 新增 `Bound`（实体承载对象；卡牌↔实体桥接缝）
- `src/Orc/Core/Model/Mounting.cs` — `WrapRegistry.RegisterJudge`（内联判定器包装器登记）
- `src/Orc/Core/UnifiedServices.cs` — `ServiceEntry.ModelJudgeWrapSource`／`ModelJudgeWrapSource` 执行链、
  `ArgsFor`／`HostOf`（宿主回填）、`Invoke` 接入模型判定器包装器
- `src/Orc/Core/JudicatorRegistry.cs` — `ModelWraps`（新模型包装面接入点）

**游戏层**
- `src/Orc.Game/Effects/EffectRuntime.cs` — `SelectAsync` 三分派、`SubmitAsync`、`ResolveDeterministic`／`ResolveRandom`；
  删除 `BeginTargetSelectionAsync`；构造器新增末位可选 `allowRandom`；随机取样登记（审查面）
- `src/Orc/Core/LogicEngine.cs` — `RandomSelections`＋`RandomSelectionRecord`（R2c／R7 审查数据源）
- `src/Orc/Core/Orchestration.cs`／`src/Orc/Output/AuditChainJson.cs`／`AuditChainText.cs` — `randomSelectors` 段
- `src/Orc.Game/Judicators/EffectTargetResolveJudicator.cs` — 只做取样；裁定/随机越界＝明确失败
- `src/Orc.Game/Match/MatchOptions.cs` — `AllowRandomTargeting`（R2c／A-5）
- `src/Orc.Game/Match/Match.cs` — 接入 `Judicators.ModelWraps`＋`allowRandom`
- `src/Orc.Game/Cards/EffectSystem.cs` — `judicatorWrappers` 落 `WrapRegistry`（同一注册面）
- `src/Orc.Mcp/McpService.SandboxMatch.cs`／`McpService.MatchLaunch.cs` — 沙盒/Authoring 开启随机

**测试（新增 11 条，无删除）**
- `tests/Orc.Tests/JudgeWrapExecutionTests.cs`（6）
- `tests/Orc.Game.Tests/EffectSelectorChannelTests.cs`（6）
- `tests/Orc.Game.Tests/Baselines/contract-freeze.json` — 重新生成（差异**仅**删除 `BeginTargetSelectionAsync` 一行 + 头部 commit/时间）

**文档**
- `docs/卡牌DIY开发文档/09-新编排模型与术语迁移.md`（新章）
- `docs/卡牌DIY开发文档/{README,01,02,03,04,07,08}.md`、`reference/信号表.md`
- `docs/前端适配-后端变更-20261020.md` §10（前端增量契约）

## 三、测试改动（逐条：改写而非删除）

| 用例／夹具 | 改动 | 理由 |
| --- | --- | --- |
| `JudicatorTargetSelectionTests.BeginTargetSelection_...` | `BeginTargetSelectionAsync(…,"all")` → `SelectAsync(…,"one")` | 唯一入口收敛；`all` 是确定性（不产生 targeter），要验证"可选性声明约束前端候选"必须用裁定类选择器。**断言未变** |
| `DiyExampleKit.CreateMatchAsync` | 新增 `allowRandomTargeting` 形参 + 默认装 `AutoPickBridge` | 示例卡 `sel=one`（火力支援）与 `sel=random`（齐射）需要前端/随机开关；测试扮演前端。断言未变 |
| `CardPackDemoTests.CreatePackMatchAsync` | 装 `AutoPickBridge` | 内联 csx 掷弹兵用 `sel=one` |
| `EffectRuntimeEndToEndTests.Generated_Effect_...` | 该用例的 match 装 `AutoPickBridge` | 生成模板产出 `sel=one`（"对一个敌方单位"＝玩家选） |
| `CardToolchainEndToEndTests` 内联 csx | 选择器 `one` → `all`（注释说明） | 该用例验证"csx 真编译 + 落地"，不验证选择语义；无桥接时 `one` 必须挂起。行为断言（2→1）不变 |
| `CovertMechanismTests.CreateMatch`／`DiyExample08`（3 例） | `allowRandomTargeting: true` | 两处显式用 `sel=random`（R2c：测试场景显式开启） |
| `CommandTestKit` | 新增 `AutoPickBridge()`；`CreateCommandMatch` 增加 `allowRandomTargeting` | 统一"测试扮演前端"的夹具 |

**删除的测试：0 条。**

## 四、被证伪 / 新确认的假设（本批）

1. **"`BeginTargetSelectionAsync` 是死代码，删掉就少一条路径"**：**部分证伪**——它确实是零调用，但其**实现体**（`TargeterManager` 全流程 + 候选判定器 + 重试）正是 `SelectAsync` 的裁定分支所需。
   删除＝**搬迁**，不是丢弃；因此"删除后 SelectAsync 改提交式"必须把整段搬过去，而不是简单转调（否则丢掉"名单快照复核"与 `InvalidSelection` 重试）。
2. **"既有样例可以原样迁到提交通道"**：**证伪（影响面）**。`samples/card-pack-diy/diy.fire-support`、`card-pack-demo` 掷弹兵、
   效果解析模板 `join_basic` 都产出 `sel=one`；旧路径"取首个"让它们在没有前端时也能跑。改提交式后这 4 类用例必须显式**扮演前端**
   （否则就是 R2 明令禁止的隐式代选）。本批以 `CommandTestKit.AutoPickBridge` 显式承担该角色。
3. **"关闭 random 只影响生产"**：**证伪**。`CovertMechanismTests`（无头选靶剔除隐蔽）与 `DiyExample08`（突袭）都依赖 `sel=random`，
   必须显式开启开关——这正是 A-5 预期的"测试/沙盒可显式开启"，但也说明**开关默认值会改变既有卡包的可运行性**（见 §七 人工确认第 2 条）。
4. **"判定器包装器只要进了 WrapRegistry 就生效"**：**证伪**。判定器调用点是 `ServiceEntry.Invoke`（`ServiceRegistry` 域桶管道），
   不认识模型；需要 `JudicatorRegistry.ModelWraps` 显式接入 + 宿主回填通道（`OrchestrationEntity.Bound`）。
   顺带确认：**效果来源的包装器与插件来源的包装器目前仍走两条入口**（前者新面、后者旧域桶，执行点在同一个 `Invoke` 里）——
   一条注册面要到 `Effect` 退役（§A）才能完全成立。

## 五、本批改变了哪些行为（R8 可指认）

1. **效果选靶**：`one`/`any` 不再隐式"取首个"，改为**必须经提交通道**；无前端＝明确异常（`InvalidOperationException`，含 `BridgeNotAssembled` 原因与修法提示）。
   确定性（`all`/`self`）路径行为不变。
2. **随机取样**：生产对局执行到 `sel=random` ＝明确异常（可经 `MatchOptions.AllowRandomTargeting` 开启）；沙盒/测试已显式开启。
3. **唯一入口**：`EffectRuntime.BeginTargetSelectionAsync` 不存在（public 面收缩一条）。
4. **判定器包装器执行**：组件资产 `judge-wraps` 与内联注入在**同一相位语义**下由判定器调用点执行；
   效果来源的包装器不再进旧域桶（改走 `WrapRegistry`），随效果 teardown 撤销的语义不变。
5. **审查导出**：新增 `randomSelectors[]`（加性；生产对局恒为空）。`schemaVersion` 仍为 2。
6. **契约基线**：`contract-freeze.json` 更新（仅删一行 + 头部），无其它 API 面变化。

## 六、未完成清单（按依赖顺序，含阻塞证据与建议做法）

### §A（阻塞项）S13 旧类型退役 + 生产装载切 effects-v4

#### §A0（本轮：S13 第一轮）`Orc.Foundation.Actions` 17 处 `new Trigger<` → **0 迁移 / 6 动作全阻塞**

**结论（一句话）**：这 17 处**不是一个可独立迁移的切片**。六个动作的触发器句柄同时承担四个身份——
①是对外「动作插件契约」的成员（6 个 `I*Action` 接口以 `Trigger<TView>` 直接声明）；
②是 `TriggerRegistry` 唯一接受的登记形态；③是 `KeywordEffects` 按 band 挂接的唯一入口；
④是 `Match` 按显示名解析 `injects[].target` 的取件口。
只换动作内部的句柄、把这四处消费留在原地，在 C# 类型层面不成立；换句柄就必须同批改 `src/Orc.Game` 的契约与注册面
→ 命中硬约束 5，六个动作**全部**标「需先解锁依赖」，本轮不迁。

**本轮改动**：源码 0 行、测试 0 条（无新增 / 无删除 / 无改写）。
**全量回归**（`dotnet test OrcEngine.sln --artifacts-path artifacts/test-s13-baseline`）：**2544 通过 / 0 失败 / 0 跳过**
（Orc.Game 1405、Orc.Tests 468、Orc.Packages 369、Orc.Mcp 118、Orc.Server 112、Orc.Script 39、Orc.Manager 20、Orc.Ai 13）
——与批次 5 基线逐项一致，证明"本轮未迁移"不是靠改测试换来的。

**逐动作阻塞证据**（行号＝当前 `HEAD` 工作区）

| # | 动作 | `new Trigger<`（行） | 新模型目标成员 | 外部强类型持有者（编译期必需） | 阻塞类 |
| --- | --- | --- | --- | --- | --- |
| 1 | `JoinBattlefieldAction` | 4（51/53/55/57） | **缺声明**：`engine#join.battlefield`／`.join.frontline`／`.join.support`／`.handle` | `IJoinBattlefieldAction.cs:19/22/25/28`；`MatchActionCatalog.cs:86-89`；`EffectRuntime.Board.cs:48/51` | B1 B2 B5 |
| 2 | `DamageAction` | 4（41/43/45/47） | 已声明 `engine#combat-damage`／`effect-damage`／`battle-damage`／`damage-apply`（`EngineProcesses.cs:38-71`，**无自有边**） | `IDamageAction.cs:17/20/23/26`；`DamagePipeline.cs:40/43/46/49`；`KeywordEffects.cs:539/597`；`Match.cs:705-708`；`MatchActionCatalog.cs:77-78` | B1 B2 B3 B4 |
| 3 | `DrawAction` | 3（48/49/50） | **缺声明**：`engine#draw.top`／`.specific`／`.handle` | `IDrawAction.cs:14/17/20`；`MatchActionCatalog.cs:75` | B1 B2 B5 |
| 4 | `MoveAction` | 2（72/73） | **缺声明**：`engine#move`／`.handle`（`EngineProcesses.UnitMove`＝「单位移动触发器」是指挥入口，**不是**本动作） | `IMoveAction.cs:29`（`Ends`）；`ActionTriggerEnd.cs:22/38`＋`TriggerRegistry.cs:23`；`MatchActionCatalog.cs:84`；`CommandManager.cs:386` | B1 B2 B5 |
| 5 | `DeathAction` | 2（43/44） | **缺声明**：`engine#death`／`.handle` | `IDeathAction.cs:13/17`；`CommandManager.cs:266`（`Handle.Register(…, ActionHandleBands.Rewrite, …)`）；`MatchActionCatalog.cs:80` | B1 B2 B3 B5 |
| 6 | `AddToHandAction` | 2（36/38） | **缺声明**：`engine#hand.add`／`.handle` | `IAddToHandAction.cs:15/18`；`MatchActionCatalog.cs:73` | B1 B2 B5 |

**六类阻塞（解锁项，按依赖排序）**

| 类 | 内容 | 证据（file:line） | 为什么不能在本轮改 |
| --- | --- | --- | --- |
| **B1** | 动作契约以 `Trigger<TView>` 声明两端 | `IAddToHandAction.cs:15/18`、`IDrawAction.cs:14/17/20`、`IDeathAction.cs:13/17`、`IDamageAction.cs:17/20/23/26`、`IJoinBattlefieldAction.cs:19/22/25/28` | 新句柄类型不满足接口签名＝编译不过；改接口＝改插件替换边界（R17 明定 `I*Action` 为固定边界），且 `CommandManager` 持有这 6 个接口 |
| **B2** | 登记面只认 `Trigger<TView>` | `TriggerRegistry.cs:23`（`Register<TView>(Trigger<TView>, TriggerLayer)`）；`ActionTriggerEnd.cs:22/38`（`Action<TriggerRegistry,TriggerLayer>`）；链路 `BuiltInPlugin.cs:178 → MatchBindingServices.cs:29/36 → MatchActionCatalog.cs:137-148 → End<TView>（:154-160）` | 任何新句柄都无处登记；加重载＝改 `src/Orc.Game` 的注册面 |
| **B3** | band 消费者是**生产代码**，不是测试 | `KeywordEffects.cs:370-388`（`AttachTo<TView>(Trigger<TView>, …, Enum band, …)`）、`:539`（`pipeline.CombatDamage`＋`ActionTriggerBands.Initiate`）、`:597`（`pipeline.Apply`＋`ActionHandleBands.Rewrite`）、`CommandManager.cs:266`（`DeathAction.Handle`＋`Rewrite`） | band 已退役（R1i），但这两处是**效果/词条系统**的挂接点；改它们的前提是 A3（词条→组件实体），属批次 3 之后的活 |
| **B4** | 显示名取件链 | `Match.cs:705-708`（4 个显示名→`DamagePipeline.*` 工厂）；`injects[].target` 解析经 `MatchNamedTriggers` | 句柄变成员后，`typeof(DamageApplyView)` 这一"形状来源"随 A4 消失；只有 A2/A4 同批才能拆 |
| **B5** | 目标成员缺声明 | `EngineProcesses.cs:33-46`（只有 9 个：command／unit-move／attack-damage／combat-damage／effect-damage／battle-damage／damage-apply／unit-attack／action-budget）；守护测试 `EngineEntityTests.cs:53`（"常量集合 == 预制体成员集合"） | 六个动作里有 5 个没有成员名；新增须改 `src/Orc.Game/Model/EngineProcesses.cs` + 守护测试 |
| **B6** | 运行时可用性未定义 | `LogicEngine.cs:125`（`Model` 可空）；生产注入时序**没问题**（`Match.cs:180-181` 早于 `:735` 建 `CommandManager`）；仅 2 处测试**独立构造**动作：`P2ActionFamilyTests.cs:95`（`MoveAction`）、`P1ActionContractTests.cs:118`（`DamageAction`） | 数量小但性质关键：这两条用例同时断言 band 内部（`P1` 断言 `Commit` 段、`P2` 断言 `Events` 的 band 值），迁移时必须按硬约束 2 改写成等价行为断言；且动作改用引擎成员后需定义"无 `Model`"语义 |

**三个语义缺口（必须先裁决，否则"迁完等价"不成立）**

| 编号 | 缺口 | 旧行为 | 新模型的冲突 | 影响面 |
| --- | --- | --- | --- | --- |
| **D1** | 默认"转发"与默认"落地"的**位次** | 触发端 `Commit`（priority 100，**最后**）；处理端 `Apply`（**最后**） | 新模型自有边**固定最前**（R1i）→ 直接搬过去，任何外来边/包装都**永不执行**。不引入 band 的候选表达只有"框架自带的 `after` 包装器（FIFO，排在边集合之后）"，但 S2a 把 `after` 定义为**作者**的尾部副作用，框架能否占用需裁决；另一选项是拆成独立成员再挂载（改变审计图形状） | 全部 6 个动作 |
| **D2** | 同段多订阅者的相对次序 | 同 band 内按 `priority` 升序＋注册序；`P3ActionFamilyTests.cs:130-136` 直接断言"部署词条(50) 先于 转发(100)" | 新模型只有声明序、无 priority。动作内两条默认边正是 50/100 的关系，而"部署词条"来自 `_services.InvokeDeployKeywordAsync`（不是挂载项）→ 声明序无从承载 | `JoinBattlefieldAction`（部署词条）；其余动作的同段多点 |
| **D3** | `before` 是 **LIFO**，旧 `Initiate/Announce` 是**升序** | 多个 `Initiate` 订阅者按 priority 升序执行 | `before` 按声明序逆序 → 同段多点的相对次序**反转**。减伤类叠加（`KeywordEffects:539` 与相邻掩护）次序反转会改变钳制点 | `DamageAction`（重甲/相邻掩护/海绵装甲叠加） |

> **前提证伪**（本轮唯一被证伪的判断）：任务书的前提"这 17 处可以只看 `Orc.Foundation.Actions` 一个目录做完"不成立。
> 真正的切片边界不是**目录**，而是"动作插件契约 + 注册面 + 效果挂接面"三者的**同一批**；把目录切出来做，
> 得到的是"编译不过"或"句柄换了但四处消费还在旧类型"，两者都比不动更差。

> **也不能"先迁一个动作试试"**：六个动作的**默认转发 / 默认落地都是链上最后一步**（`Commit` / `Apply`），
> 全部踩在 D1 上——新模型自有边固定最前，无论怎么写，外来边/包装都会跑到"转发"之后（或永不执行）。
> 这是模型层的位次缺口，不是某个动作的实现细节，所以逐动作试探得不到"等价"的结果。

**解锁后可直接照做的迁移预案**（供第二轮执行，未落地）

| 动作 | 成员（`engine#`） | 自有边（框架默认） | 旧 band → 新形态 |
| --- | --- | --- | --- |
| `AddToHandAction` | `hand.add`（通用前置）、`hand.handle`（入列表／爆牌） | 前置；落地 | `Initiate` 订阅 → `wrap before`；`Commit` 转发 → D1 裁决后落 `after`；`Apply` 外来注册 → 挂载边 |
| `DrawAction` | `draw.top`／`draw.specific`／`draw.handle` | 同上三件套 | 同上 |
| `DeathAction` | `death`、`death.handle` | 前置；免死守卫（`Veto`→`before`+`Stop`）；死亡链 | `CommandManager.cs:266` 的 `Rewrite` → `wrap before`（改写 `ShouldDie`） |
| `MoveAction` | `move`、`move.handle` | 前置；落地 | 同上 |
| `JoinBattlefieldAction` | `join.battlefield`／`join.frontline`／`join.support`／`join.handle` | 代价校验／门禁；单位化落位 | `Announce`（`card.played`）→ 声明序边；部署词条见 D2 |
| `DamageAction` | `combat-damage`／`effect-damage`／`battle-damage`、`damage-apply`（**已声明**，只需补自有边） | 转发；扣数值 | 重甲减值（`:539` `Initiate`）→ `wrap before`；免疫归零（`:597` `Rewrite`）→ `wrap before`+改写 `IsZeroed` |

**建议的解锁顺序（每步可独立验收）**

| 步 | 内容 | 验收 |
| --- | --- | --- |
| U1 | `EngineProcesses` 补 13 个成员名（hand.add/hand.add.handle、draw.top/specific/handle、death/death.handle、move/move.handle、join.deploy/join.frontline/join.support/join.handle）＋ `EngineEntityTests.cs:53` 守护同步 | ✅ **已完成**（§A1.2）：守护测试绿；`grep new Trigger<` 38→38（纯声明，行为等价） |
| U2 | 注册面加宽：`TriggerRegistry` 改登记"成员地址"（或加按成员句柄登记的重载）、`ActionTriggerEnd` 承载成员句柄、`MatchActionCatalog.End<TView>` 去泛型 | ✅ **已完成**（§A1.3）：`TriggerRegistryTests` 由"实例同一性"加宽为"地址同一性"（实例断言保留至 U5）；19/21 端带地址 |
| U3 | 契约加宽：6 个 `I*Action` 属性 → 成员句柄；`DamagePipeline`／`EffectRuntime.Board` 同批 | 六测试项目全绿；`new Trigger<` 仅剩 B3/B5 相关文件 |
| U4 | D1–D3 裁决后落"转发/落地"的位次（建议 `after`），逐动作补 ≥3 条等价测试（段次序／`Stop` 语义／结果位） | 每动作的"发起→宣告→反制→调整→提交"外部行为与迁移前一致 |
| U5 | band 消费者下线（`KeywordEffects:539/597` 依赖 A3；`CommandManager:266` 同批）＋ `Match.cs:705-708` 显示名工厂随 A2/A4 删除 | `grep "ActionTriggerBands\.\|ActionHandleBands\." src/` = 0 |

**新增风险（本轮发现，非阻塞但需裁决）**

1. **类型保真度**：新视图类型是封闭集（`ViewSpec.cs:13-38`），动作载荷含 `FormationGap`（结构体）、
   `ActionTargetResult`（结果载体）、`ActionDisposition`（枚举）。`ViewFieldTypes.Accepts(Entity, …)` 对任意非 null 对象放行
   （`ViewSpec.cs:118`），因此**能跑**，但静态校验退化为"非 null 对象"——是否需要为动作载荷引入专用字段类型，属 R1a 边界问题。
2. **`Result` 字段的写入契约**：旧视图的 `Result` 是 `[Read]` 共享引用（handler 直接调 `MarkApplied`）。
   新模型若把它声明为只读 `entity` 字段，读到的仍是同一对象（引用语义不变），但"写只读字段＝拒绝"的静态检查会拦住任何
   `view.Result = …` 写法——迁移时须保持"只调方法、不赋值"的既有写法。
3. **审计面形状变化**：动作两端从"触发器实例"变成"引擎成员"后，`TriggerRegistry` 的"两端齐备"校验
   （`MatchActionCatalog.Validate`：恰 1 处理端）需要新的等价判据（按成员角色标注），否则 R5/R8 的装配期 fail-fast 会失效。

**本轮执行记录（复盘）**

1. 读 implementation S13／PROGRESS §A／requirements R1i·R1e·R6b → 确认"band 退役 + 成员＝有序边"的落地口径。
2. 清点 17 处 `new Trigger<`（6 文件）→ 逐动作 grep 外部消费者 → 命中 6 个 `I*Action` 契约。
3. 全量回归建基线（2544/0/0）→ 与批次 5 记录逐项一致。
4. 判定"目录切片不可独立成立"→ 不写代码，按硬约束 5 落阻塞清单与解锁顺序（本节）。

---

**现状与证据（命令可复跑）**

```text
grep -rn "new Trigger<" src/ | wc -l                                  → 38（17 文件）
grep -rn "Trigger<" src/ --include=*.cs | wc -l                       → 193（+1＝U2 异常消息里的类型名，非实例化点）
grep -rn "Trigger<" tests/ --include=*.cs | wc -l                     → 217（U0–U2 未新增测试侧引用）
grep -rn "\bBus\b" tests/ --include=*.cs | wc -l                      → 123
grep -rn "DynamicEffect|ActiveEffect|PassiveEffect|.Effects|AddEffect|EffectSystem|EffectPrefab|EffectSnapshot" tests/ → 605 处
```
分布：`CommandManager` 7、`Orc.Foundation.Actions` **17**（6 文件：JoinBattlefield 4／Damage 4／Draw 3／Move 2／Death 2／AddToHand 2）、
`CardLifecycle` 2、`OrderFlow` 2、`Effect` 2、
`CardMemberTable` 2、`SandboxPrimitives`/`Hq`/`RetriggerSystem`/`UnitCard`/`OrderCard`/`CounterCard` 各 1。
（implementation S7 记的是"15 处"，实测 17 处——以代码为准，见 §A0）

**为什么本批不能"删了就绿"（三步硬依赖，缺一即红）**

1. **C# handler 的视图类型是旧类**（`Func<CardTriggerView, Context, CancellationToken, Task>`）。
   新模型的 handler 形态是 `KernelHandler(object view, …)`，`view` 是 `ViewCarrier`（动态载体），**不能**传给强类型委托。
   因此删 `Trigger<TView>` 之前，必须先把卡牌/动作/引擎三层的 C# handler 全部改成 `dynamic`／`ViewCarrier` 形态
   （`CardBase`/`UnitCard`/`OrderCard`/`CounterCard`/`Hq`、`CommandManager` 7 处、`Orc.Foundation.Actions` 17 处、
   `CardLifecycle`/`OrderFlow`/`RetriggerSystem`/`SandboxPrimitives` 等），并同步改 217 处测试引用。
2. **生产信号发射仍走旧总线**：`GameUpdates`（35 条）＋`Updates`（4 条）的 `Emit` 是既有生产路径；
   新模型的 `EmitAsync` 只在测试与审查导出里用。删 `Bus` 前必须有"Emit＝调用 engine#成员"的生产接线
   （S3 第 2 条），否则效果全部不触发。
3. **效果装载/执行仍在 `EffectSystem` + `Effect` 子类**（605 处测试观测点）。
   切 `effects-v4` 需要把"卡数据 → 组件实体 → `ComponentAttachment.Assemble`"接进 `CardBase` 装配链，
   并把 `Effect` 的观测面（`card.Effects`／`IsMounted`／teardown）迁到 `ComponentAttachment`／审查边图；
   否则 §A2 一落地就是大面积红。**这正是批次 4 交接里说的"卡牌实例 ↔ OrchestrationEntity 桥"**——
   本批只落下第一块拼板（`OrchestrationEntity.Bound`）。

**建议的下一步（可分批、每批全绿）**

| 步 | 内容 | 验收 |
| --- | --- | --- |
| A1 | 卡牌侧建宿主实体：`CardBase` 构造期 `runtime.CreateEntity(cardPrefabId, name)` 并 `Bound = this`；`Hq` 同 | 卡实体可枚举、审查面出现卡实体；旧执行不变（纯加性） |
| A2 | 信号发射双写收敛：`LogicEngine.Emit` 改为"调 `engine#<信号>` 成员"，旧总线订阅面改由新模型边表承载 | 既有 1399 条游戏层测试全绿（这一步即 S3 的生产收口） |
| A3 | 效果装载切 `effects-v4`：`CardPackAssembler` 选 v4 目录 → `AssetPackLoader`；卡加载时 `ConstructComponent`+`Assemble` | 20 份样本等价性测试扩到"卡上真实生效"；旧 `Effect` 装载路径下架 |
| ~~A4a~~ | ~~`Orc.Foundation.Actions` 17 处 `new Trigger<` 独立迁移~~ | ❌ **本轮证伪：不可独立迁移**（6 动作全阻塞，0/17）→ 见 §A0；改由 U1–U5 解锁，且必须在 A3 之后 |
| A4 | 删旧类型：`Trigger<TView>`／`Effect`／`EffectPrefab`／`EventPrefab`／`TriggerPrefab`／`Bus`／`HookRegistry`／`PrefabManager`／`PrefabJson`／`MatchNamedTriggers`／`LegacyDisplayNames`／`TryFindNamedTrigger`／`AuditGraphBuilder.CollectLegacy*` | ◐ **U5-b 只完成 band 面（§十五）**：`new Trigger<` = 38、`Bus` = 40，阻塞于 A2／A3；全量 2602 / 0 / 0 |
| A5 | 测试观测面改写：605 处 `Effect*` 断言 → 组件/审查边断言（按 S14 第 5 条口径：断"审查面上存在某条边"，不依赖显示名） | 全量绿；逐条在 PROGRESS 记录改写理由 |

> 依赖提醒：A4 会同时删掉 `PrefabJson` 的 DTO，因此 `contract-freeze.json` 的 `prefabJsonDtos` 段会整体消失——
> 需要与前端约定"该段随 v4 下线"（前端当前未消费该段）。

### §A1（本轮）U0→U1→U2 解锁路径落地

#### §A1.1 U0 模型修订（已完成，全量绿）

**改了什么语义（R8 可指认）**

| # | 语义 | 改前 | 改后 | 依据 |
| --- | --- | --- | --- | --- |
| 1 | 自有边位次 | 恒最前（"自有边→外来边→框架边"） | 由 `MemberDefinition.OwnEdgePosition` 决定：`First`（默认）＝自有边→外来边；`Last`＝外来边→自有边 | R1i 修订（D1 裁决）；不改则动作管道的"转发/落地"会让外来边永不执行 |
| 2 | `before` 相位 | LIFO（声明序逆序） | FIFO（声明序正序） | R1i 修订（D3 裁决）；与旧 `Initiate`/`Announce` 的 priority 升序等价，避免减伤叠加钳制点反转 |
| 3 | `after` / `replace` | FIFO / 最后声明者 | 不变 | D3 |

- 落点：`src/Orc/Core/Model/Members.cs`（`OwnEdgePosition`＋成员声明属性）、
  `OrchestrationEntity.RunEdgesAsync`（按位次排序；抽 `RunOwnEdgesAsync`）、
  `Mounting.cs`（`BeforeExecutionOrder` 去 `Array.Reverse`＋文档）、`JudicatorRegistry`/`UnifiedServices` 注释同步。
- **未改**：旧域桶（`src/Orc/Core/Wrapper.cs`／`ServiceEntry.SnapshotBefore`）的 `before` 仍是历史 LIFO。
  理由：那是 `Trigger` 的包装面，随 U5／A4 与旧类型一并退役；它不表达"band 段内次序"，不存在 D3 要修的等价问题。
  代价（记入风险）：过渡期内同一判定器上"新面 FIFO + 旧桶 LIFO"并存，**跨两面的相对次序不构成契约**（已在代码注释与 §八 标明）。

**测试改动（逐条改写理由；无删除）**

| 用例 | 改动 | 理由 |
| --- | --- | --- |
| `WrapChannelTests.before按LIFO_...` → `before按FIFO_after按FIFO_replace取最后声明者` | 名字改 FIFO；期望 `before2,before1` → `before1,before2` | 断言对象从"LIFO 次序"换成"FIFO 次序"，验证的仍是同一件事（相位内部的相对次序＝声明序） |
| `WrapChannelTests.免疫命中后after包装器与后续边不执行且已执行的before写入保留` | `write` 与 `immunity` 的 `DeclarationOrder` 互换（`write` 0、`immunity` 1） | FIFO 下若保持原序，`immunity` 先跑并 `Stop`，`write` 永不执行→"已执行 before 的写入保留"无从验证。交换后语义不变：仍是"先跑一个写，再被免疫停住" |
| `WrapChannelTests.判定器包装器与成员包装器共用同一相位语义与同一注册面` | 期望 `h.j2,h.j1` → `h.j1,h.j2` | 同一注册面的 before 次序随 D3 改 FIFO；"共用同一相位语义"这层断言不变 |
| `JudgeWrapExecutionTests.判定器包装_..._before按声明序逆序_...` → `..._before按声明序正序_...` | 期望 `before2,before1,core` → `before1,before2,core` | 同上（判定器包装器与新面同源） |
| **新增** `tests/Orc.Tests/Model/OwnEdgePositionTests.cs`（3 条） | `默认位次为first_自有边先跑_外来边续后`／`声明last_外来边先跑_自有边最后`／`last位次下多条外来边仍按秩次执行_自有边恒最后` | D1 的新语义此前零覆盖（旧模型没有这个概念）；三条钉住默认值、Last 的位次、以及 Last 下外来边内部仍按秩次 |

**全量**：2547 通过 / 0 失败 / 0 跳过（Orc.Tests 468→471）。

#### §A1.2 U1 补成员声明（已完成，全量绿）

- `EngineProcesses` 新增 13 个成员名（`hand.add`／`hand.add.handle`／`draw.top`／`draw.specific`／`draw.handle`／
  `death`／`death.handle`／`move`／`move.handle`／`join.deploy`／`join.frontline`／`join.support`／`join.handle`），
  `MemberNames` 由 9 → **22**，与预制体成员集合仍由 `EngineEntityTests.EngineMemberConstants_MatchPrefabMembers` 钉住。
- **位次标注（D1）**：落地型 7 个＝`OwnEdgePosition.Last`（5 个 `*.handle` ＋ `damage-apply` ＋ `attack-damage`）；
  流程型 15 个保持默认 `First`（含 `join.deploy`/`frontline`/`support` 三个入口、`hand.add`、`draw.top`/`specific`、`death`、`move`）。
  依据：`attack-damage` 是 `IAttackAction.Handle`（旧 `Rewrite`/`Veto`/`Apply` 段＝落地），`unit-attack` 是转发端 → `first`。
- **视图声明留空**：这 13 个成员暂为 `ViewSpec.Empty`——载荷字段集在 U3/U4 逐动作迁移时按旧 C# 视图类补齐
  （与已声明的 `damage-apply` 的 `damageApply` 视图同源）。**纯声明，零行为变化**：`new Trigger<` 计数不变。
- 新增守护测试 `EngineEntityTests.ActionPipelineMembers_AreDeclared_WithOwnEdgePositionByKind`：13 名皆可解析、
  7 个 `last`、8 个 `first` 逐条断言（漂移即红）。
- **全量**：2548 通过 / 0 失败 / 0 跳过（Orc.Game 1405→1406）。

#### §A1.3 U2 注册面加宽（已完成，全量绿；**未触碰 U3 边界**）

**改了什么（模型语义）**

| # | 面 | 改前 | 改后 |
| --- | --- | --- | --- |
| 1 | `TriggerRegistry` 的**登记身份** | 只有触发器实例（`ReferenceEquals` 同一性） | **成员地址**（`engine#hand.add`…）为一等身份：`Register(MemberAddress, layer)`／`Register(object, MemberAddress, layer)`；`Find(address)`；重复地址＝拒绝 |
| 2 | `TriggerRegistryEntry` | `Trigger`（object） | `Trigger`（object?，旧身份）＋`Member`（`MemberAddress?`，新身份）＋`Address`（原文） |
| 3 | `ActionTriggerEnd` | 角色＋实例＋登记委托 | 追加 `Member`（`MemberAddress?`）＋标准登记构造（有地址＝一条**双身份**登记项） |
| 4 | `MatchActionCatalog.End<TView>` | 泛型，按 `Trigger<TView>` 闭包登记 | **非泛型**（`object`＋引擎成员名常量）；19/21 个端带地址，指令动作 2 端为 `null` |

- 保留旧面的理由（硬约束）：6 个 `I*Action` 契约仍以 `Trigger<TView>` 声明两端，**U3 才加宽**；
  因此实例身份在 U5 前必须继续进账（双身份同账 = 一条登记项，不是两条——所以"21 项"计数不变）。
- **未改**：6 个 `I*Action` 契约、`DamagePipeline`、`KeywordEffects`、`new Trigger<`（仍 38 处／17 文件）。
- 成员名来源＝`EngineProcesses` 常量（R1j 同源表）：动作端 → 成员名的映射在 U3 会**上移进契约**（实现自己报的
  成员句柄）；本步先落在目录里，作为"装配真源"的临时落点。

**U2 的边界（为什么不算"卡在 U3"）**

- 编译不依赖 U3：注册面加宽是**加性**的，旧调用点（`CardLoaderTrigger.RegisterIn`、插件端自报）零改动即通过。
- 唯一遗留是**语义缺口**：`Order` 动作两端（`engine.OrderFlow.Trigger`／`FinalizeTrigger`）是**内核 flow** 的触发器，
  不是引擎实体成员 → 没有成员地址可填（`End(..., (string?)null)`）。
  这不是编译阻塞，而是**归属决策**：`order.cast`/`order.finalize` 要不要成为引擎成员，取决于 U3 把 `IOrderAction`
  的两端改成什么（成员句柄 or 保留 flow 触发器）。本步**不擅自新增**这两个成员名（U1 的 13 个是父 agent 给定清单）。

**测试改动（逐条改写理由；无删除）**

| 用例 | 改动 | 理由 |
| --- | --- | --- |
| `TriggerRegistryTests.Register_And_Enumerate_By_Layer` | 注册改用 `Register(trigger, address, layer)`；追加"按地址可查（`Find`）"与"地址随枚举导出"断言；原实例同一性断言保留 | 登记身份从"实例"升为"地址"（R1d）；实例断言保留＝双身份同账的证明，U5 删实例面时这些断言随之删除 |
| `TriggerRegistryTests.Duplicate_And_Invalid_Registrations_Are_Rejected` | 追加：重复地址拒绝、**换实例但同地址**拒绝、地址登记遇非法分层拒绝、登记数仍为 2 | 地址成为唯一身份后，去重规则必须在地址轴上成立，否则"同址两条账"会让审查面重复计数 |
| `TriggerRegistryTests.Match_Registry_Is_Per_Match_And_LowLevel_Flow_Triggers_Are_Registered` | 追加：21 个端里 19 个带地址且互不重复、逐条 `Find` 命中、`move`/`move.handle` 由插件自报、指令两端无 `order.*` 地址 | 把"按目录装配"的验收从"21 个实例"扩到"21 条登记项 × 19 个地址"，并显式钉住 U3 前的边界 |
| **新增** `TriggerRegistryTests.AddressOnly_Registration_IsAddressable_WithoutLegacyIdentity` | 纯地址登记：`Trigger` 为 null、`Member`/`Address` 可读、`Find`/`GetByLayer` 正常 | R1e「运行时注册后可寻址」在注册面上的最小形态，此前零覆盖 |

**全量**：2549 通过 / 0 失败 / 0 跳过（Orc.Game 1406→1407）。

### §A2（本轮）U3 契约加宽：6 个 `I*Action` 两端 → 成员句柄

**契约形状（唯一真问题的裁决：候选 A＝`MemberAddress`）**

| 面 | 改前 | 改后 |
| --- | --- | --- |
| `IAddToHandAction`／`IDrawAction`／`IDeathAction`／`IMoveAction`／`IJoinBattlefieldAction`／`IDamageAction` 的端 | `Trigger<TView> X { get; }` | `MemberAddress X { get; }`（＝`engine#<成员名>`，`EngineProcesses` 同源常量） |
| 端清单 | 除 `IMoveAction` 外都靠宿主按视图类型闭包 | **每个动作统一 `IReadOnlyList<ActionTriggerEnd> Ends`**（角色＋地址＋旧身份实例） |
| 装配（`MatchActionCatalog`） | `End(role, X.Prop, EngineProcesses.Y)`——宿主维护"端→成员名"映射 | `X.Ends.ToArray()`——**地址由实现自报**，目录不再认识视图类型／成员名 |
| 旧身份实例的取回 | 契约直接给出 | `ActionEnds.TriggerOf<TView>(X.Ends, X.Prop)`（`src/Orc.Game/Actions/ActionEnds.cs`，U5 删除） |

- 实现内部保留 `<c>Trigger&lt;XView&gt;</c>` 私有字段（`_trigger`／`_handle`…）：它们仍是旧执行面与 band 挂接点的唯一实例源，
  公开面只暴露地址。**`new Trigger<` 计数不变**（17 处仍在动作实现内）——见「§A2.4 未达成项」。

**逐动作进度（每步一次全量）**

| # | 动作 | 状态 | 全量结果 | 等价测试（`ActionContractWideningTests`） |
| --- | --- | --- | --- | --- |
| 1 | `hand.add` | ✅ | **2552 / 0 / 0**（Orc.Game 1407→1410） | `加入手牌_两端地址同源且已登记`／`加入手牌_外部按地址注册仍生效`／`加入手牌_行为等价_入列表恰一次且重复加入被拒` |
| 2 | `draw` | ✅ | **2555 / 0 / 0**（Orc.Game 1410→1413） | `抽牌_三端地址同源且已登记`／`抽牌_两触发端实例互不相同且按地址可分别挂接`／`抽牌_行为等价_离组入列表且drawn先于handadd` |
| 3 | `death` | ✅ | **2558 / 0 / 0**（Orc.Game 1413→1416） | `死亡_两端地址同源且已登记`／`死亡_改写位按地址注册仍可取消死亡`／`死亡_行为等价_死亡链照常落地` |
| 4 | `move` | ✅ | **2560 / 1（已知抖动）/ 0**；`Orc.Mcp.Tests` 单项目复跑 **118/0**（见 §八 flaky） | `移动_两端地址同源且已登记`／`移动_外部按地址注册前置仍可阻断`／`移动_行为等价_落地到目标阵线且位置信号恰一次` |
| 5 | `join` | ✅ | **2564 / 0 / 0**（Orc.Game 1416→1422） | `加入战场_四端地址同源且已登记`／`加入战场_三触发端各自独立且按地址注册前置仍生效`／`加入战场_行为等价_部署落地且placed恰一次` |
| 6 | `damage` | ✅ | **2567 / 0 / 0**（Orc.Game 1422→1425） | `伤害_四端地址同源且已登记`／`伤害_门面实例与动作自报实例同一`／`伤害_行为等价_效果伤害扣减且信号恰一次` |
| 7 | `order` | ✅ | **2570 / 0 / 0**（Orc.Game 1425→1428） | `指令_两端升为引擎成员且按地址登记`／`指令_按地址注册仍可观测到施放`／`指令_行为等价_宣告扣费离手` |

**U3 终局全量（`--artifacts-path artifacts/test-s13-u3-order`，终态复跑 `artifacts/test-s13-u3-final` 同数）：2570 通过 / 0 失败 / 0 跳过**
（Orc.Game 1428、Orc.Tests 471、Orc.Packages 369、Orc.Mcp 118、Orc.Server 112、Orc.Script 39、Orc.Manager 20、Orc.Ai 13）。
基线 2549 → **+21 条新测试**（7 个动作 × 3 条），**0 删除、0 跳过**。`new Trigger<` 全程 **38 处 / 17 文件（未变）**。

#### §A2.1 同批改的两个持有者 ＋ 三处接入路径（band 语义一律不动）

| 落点 | 改动 | 为什么必须同批 | 为什么不改语义 |
| --- | --- | --- | --- |
| `DamagePipeline` | 四个 `<c>Trigger&lt;…&gt;</c>` 属性改为**构造期从 `_action.Ends` 按地址取回**并固定 | 契约不再交出实例；门面是重甲／免疫与显示名取件的唯一入口 | 取回的就是**同一对象**：`伤害_门面实例与动作自报实例同一` 逐条 `Assert.Same`；属性类型、属性名、段与 handler 全不变 |
| `EffectRuntime.Board` | `JoinFrontlineTrigger`／`JoinSupportLineTrigger` 改为按地址取回（未装配＝`null` 不变） | 同上（`IDrawAction`/`IJoinBattlefieldAction` 不再给实例） | 返回类型与 null 语义不变（`contract-freeze.json` 的 `effectRuntime` 段零差异）；这两条当前**零消费点**，风险为零 |
| `CommandManager.cs:266`（死亡处理端 `Rewrite` 改写位） | `DeathAction.Handle.Register(…)` → `ActionEnds.TriggerOf<DeathHandleView>(DeathAction.Ends, DeathAction.Handle).Register(…)` | 契约不再交出实例，**不改就编译不过** | **同一实例、同一段（`ActionHandleBands.Rewrite`）、同一 priority（`DeathRewritePriority`）**；`KeywordProgramTests`／`P2`／`P3` 的改写位断言全绿即证据。band 下线仍留 U5 |
| `KeywordSystem.cs`（`KeywordLoadContext.DamageAction`） | 类型 `IDamageAction?` → `DamagePipeline?`（属性名不变） | 重甲的 `pipeline.CombatDamage`、免疫的 `pipeline.Apply` 需要实例 | **`KeywordEffects.cs:537/595/539/597` 一个字符都没改**（契约面收窄到门面，两处调用原样编译）；`BulwarkKeywordTests`／`KeywordEffectBatch2Tests` 全绿 |

#### §A2.2 测试改动（逐条：改写而非删除；断言强度只增不减）

| 用例／夹具 | 改动 | 理由 |
| --- | --- | --- |
| `TriggerRegistryTests.Match_Registry_…`（21 端） | `ReferenceEquals(entry.Trigger, action.X)` → 新助手 `AssertEndRegistered(registry, ends, address)`＝"按地址命中 + 账上实例与自报实例同一" | 断言对象仍是**同一实例**（旧机制），另加**地址**这一新身份；21 端由 19 个地址扩到 **21/21** |
| 同上（19→21 计数、`DoesNotContain order.cast`） | 19→21、去重数 19→21，并把"指令两端无地址"的边界断言改为"已在账上" | U3 裁决把 `order.cast`／`order.finalize` 升为引擎成员，U2 留档的边界消失 |
| `P1ActionContractTests.Damage_Action_Registers_Commit_Once_Per_Trigger_End` | `action.CombatDamage.Events` → `ActionEnds.TriggerOf<DamageEventView>(action.Ends, action.CombatDamage).Events` | 同一实例的同一段／事件；契约面不再暴露实例，取回路径与宿主一致 |
| `P2ActionFamilyTests.Move_Action_Registers_…` | 同上（`MoveActionView`） | 同上 |
| `P2ActionFamilyTests.Add_To_Hand_Action_Is_Reusable_And_Has_Both_Ends` | 同上（两视图） | 同上 |
| `P2ActionFamilyTests` 3 处 `DeathAction.Handle.Register(…)`／`KeywordProgramTests` 1 处／`P3ActionFamilyTests` 1 处 | → `…HandleOf().Register(…)`（`ActionEndTestKit`，内部走 `ActionEnds`） | 测试扮演的正是"宿主/词条面的改写注册"，改法与生产同源；段与 priority 未变 |
| `P3ActionFamilyTests.Death 伏击探针` | `DeathAction.Trigger.Register` → `TriggerOf().Register` | 同上 |
| `P3ActionFamilyTests.Join_And_Deploy_Share_One_Handle_End…` | `action.Deploy.BandType` 等 → 经 `Ends` 解引用后断言 | 断言内容（band 方案类型、Apply 段证据、视图无 Mode 标志）逐条不变 |
| `P3ActionFamilyTests.Deploy_Keywords_Run_In_Commit_Head…` | `Deploy.Events` → 经 `Ends` 解引用 | 断言"部署词条(50) 先于转发(100)"不变 |
| `EngineEntityTests.EngineMemberConstants_MatchPrefabMembers` | 22→**24** 个流程成员 | `order.cast`／`order.finalize` 新增声明（同源表仍由该测试锁漂移） |
| `EngineEntityTests.ActionPipelineMembers_AreDeclared_WithOwnEdgePositionByKind` | 13→**15** 声明；`last` 加 `order.finalize`、`first` 加 `order.cast` | U3 新增两个成员，位次按"收尾＝落地型／施放＝流程型"标注 |
| **新增** `tests/Orc.Game.Tests/ActionContractWideningTests.cs`（21 条） | 7 个动作各 3 条（端/地址/登记、旧身份仍可挂接且生效、端到端行为） | U3 的等价证据集（见上表逐动作列名） |
| **新增** `tests/Orc.Game.Tests/ActionEndTestKit.cs` | `HandleOf`／`TriggerOf`（内部走 `ActionEnds`） | 测试侧不发明私有捷径：与宿主同一解析路径 |

**删除的测试：0 条。** `contract-freeze.json` **未重生成**（`effectRuntime` 段形状不变，冻结测试绿即是证据）。

#### §A2.3 本轮确认／证伪的假设

1. **"把 `Ends` 统一成端清单就能让目录与实现解耦"**：**成立**。8 条 entry 全部改为 `X.Ends.ToArray()`；目录里只剩**攻击**仍用 `End(role, instance, member)` 映射（`IAttackAction` 不在本轮 6 个契约内，两端由宿主建本体传入）——这是 U4 的候选收口项。
2. **"`KeywordLoadContext` 必须改成管道门面才能不碰 `KeywordEffects`"**：**成立（且是本轮最省风险的解法）**。改 1 个类型声明，`KeywordEffects` 539/597 的 band 挂接点零改动。
3. **"`CommandManager:266` 可以不动"**：**证伪**。契约一旦只给地址，`DeathAction.Handle.Register` 就编译不过；但改动只限**接入路径**（同一实例／同一段／同一 priority），band 语义与 U5 范围均未变。
4. **"`new Trigger<` 会在本轮归零"**：**证伪**（见 §A2.4）。

#### §A2.4 未达成项：`new Trigger<` 归零（U5 依赖，非本轮可解）

```text
grep -c "new Trigger<" src/Orc.Foundation.Actions/*.cs
  AddToHandAction 2 / DrawAction 3 / DeathAction 2 / MoveAction 2 /
  JoinBattlefieldAction 4 / DamageAction 4 / OrderAction 0 / AttackAction 0      → 17
grep -c "new Trigger<" src/Orc/Cards/OrderFlow.cs                                → 2
grep -rn "new Trigger<" src | wc -l                                              → 38（17 文件，与 U0 前一致）
```

**为什么不是"没做"，而是"这一层做不了"**：
- 动作的旧执行面就是 `<c>Trigger&lt;TView&gt;</c>` 上的 `InvokeAsync`／`Register`；契约加宽后实例**仍然必须存在**（登记项双身份、band 挂接点、显示名取件链都用它）。
- 实例的**视图类型是 C# 类型**（`AddToHandTriggerView`…）。除实现自己之外，唯一可能的创建点是"宿主侧的地址→视图类型映射表"——那正是本轮刚刚从目录里**删掉**的东西，等于把同一份映射换个文件复活，并让两条独立构造路径（`new MoveAction(new LogicEngine(), …)`、`new DamageAction(…)`）失去实例来源（B6）。
- 因此归零的**真实前置**是 U5：删掉 `Trigger<TView>`／band／显示名取件链之后，成员只剩"地址 + 边集合"，不再需要 C# 视图类型。

**建议的解锁顺序（U4 之后）**：① `Match.cs:705-708` 的四个显示名工厂随 A2/A4 下沉；② `KeywordEffects` 两处 band 挂接改 `wrap before`（依赖 A3 的词条→组件实体）；③ `CommandManager:266` 改 `wrap before`；④ 才删 `Trigger<TView>` 与 `new Trigger<`。`OrderFlow.cs` 的两处同样属第 ④ 步（内核 flow 的触发器实例届时由成员地址直接调用取代）。

#### §A2.5 U3 改变了哪些行为（R8 可指认）

1. **插件契约形态（唯一对外可见变化）**：`IAddToHandAction`／`IDrawAction`／`IDeathAction`／`IMoveAction`／`IJoinBattlefieldAction`／`IDamageAction`／`IOrderAction` 的端从 `Trigger<TView>` 变为 `MemberAddress`，并统一新增 `Ends`。
   → 影响面：**任何实现/替换这些接口的插件**（`ActionImplementationSet` 的工厂）。仓库内唯一实现集＝`FoundationActions.Set`，已同批更新；
   插件若自行持有触发器实例，必须改为在 `Ends` 里自报（形态见 `MoveAction`，U2 起即如此）。
2. **引擎成员表 +2**：`engine#order.cast`／`engine#order.finalize`（审查面与预制体成员集合随之可见）。
3. **运行期行为：无变化**。段、priority、handler 次序、信号序列、结果语义全部不变——证据＝每动作 3 条等价测试 + 既有 2570 条全绿。

#### §A2.6 接续建议（U4）

1. **U4 的主体不是本轮的契约面，而是 D1–D3 的位次落地**：`OwnEdgePosition` 已就位（U0/U1），现在缺的是"哪个成员装哪些边"的装配真源——
   建议先做**攻击动作**（`IAttackAction` 两端仍由宿主建本体、目录仍有唯一的映射特例），把它并进 `Ends` 后目录彻底无 `EngineProcesses.*`。
2. **等价测试的观测面需前移**：本轮 21 条等价测试多数仍在旧 band 语义上断言（段名／`BandValue`）。U4 改"转发/落地"位次时，这些断言要改成
   **边序断言**（`OwnEdgePositionTests` 的口径），否则会与 band 一起失效。
3. **`TriggerRegistryTests` 的 21/21** 是 U5 的现成验收锚点：U5 删实例面时，只需把 `AssertEndRegistered` 的"同一实例"半句删掉。

### §A3（本轮）U4 动作位次落地与行为等价（**完成**；全量 2591 / 0 / 0）

**口径（D1／D2 的执行形态）**：`own-edge-position` 的判据是**"这一端的自有边是不是链上末步"**，不是
"端在动作里的位置"。旧 `Commit`（转发）与旧 `Apply`（落地）都是末步 ⇒ **21 端全部 `last`**。
`first` 组在动作端为空集，原因不是漏标：旧 `Initiate`／`Announce`／`CounterGuard` 是**段内相位**，不是独立端——
新模型里它们由 `before` 相位承载（R1i 的 `Veto → before+Stop`／`Rewrite → before` 同源）。
若将来把门禁**拆成独立成员**（R1i「需要中间插入就拆成员」），那个成员才应标 `first`。

**位次标注表（21/21；`EngineProcesses.ActionEndPositions` 为唯一真源，`Build()` 由它取位次）**

| 组 | 端（`engine#`） | 角色 | 旧末步段 | 位次 |
| --- | --- | --- | --- | --- |
| hand.add | `hand.add` / `hand.add.handle` | 触发端／处理端 | Commit 转发／Apply 落地 | last／last |
| draw | `draw.top` / `draw.specific` / `draw.handle` | 触发端×2／处理端 | Commit 转发×2／Apply 落地 | last×3 |
| death | `death` / `death.handle` | 触发端／处理端 | Commit 转发／Veto＋Apply | last／last |
| move | `move` / `move.handle` | 触发端／处理端 | Commit 转发／Apply 落地 | last／last |
| join | `join.deploy` / `join.frontline` / `join.support` / `join.handle` | 触发端×3／处理端 | Commit（词条＋转发）／Commit／Commit／Apply | last×4 |
| damage | `combat-damage` / `effect-damage` / `battle-damage` / `damage-apply` | 触发端×3／处理端 | Commit×3／Apply | last×4 |
| order | `order.cast` / `order.finalize` | 触发端／处理端 | Resolve 结算／Finalize 收尾 | last／last |
| attack | `unit-attack` / `attack-damage` | 触发端／处理端 | Commit 转发／Apply 落地 | last／last |

- 非动作端点（`command`／`unit-move`／`action-budget`）保持默认 `first`，不受该表影响。
- 表项带 `Role`／`OldTail`／`Rationale` 三段可读信息（旧段只作迁移留档，新模型只看位次）。
- 守护：`EngineEntityTests.ActionPipelineMembers_AreDeclared_WithOwnEdgePositionByKind` 逐条比对
  「表 ↔ 引擎成员声明」，并断言「表集合 == `MemberNames` − 3 个非动作成员」。

**D2（同段相对次序）**：`JoinBattlefieldAction` 的 `DeployKeywordPriority = 50` **删除**——部署词条与转发
同在 `Commit` 段、同 `priority`，次序由**声明序**承担（`Trigger.SnapshotSorted`：band → priority → seq）。
`P3ActionFamilyTests.Deploy_Keywords_Run_In_Commit_Head_Before_Unitization` 的
`commit[0].Priority < commit[1].Priority` 改为 `Equal`（断言对象从"数字差"换成"声明序"，次序断言不变）。

> **未纳入 D2 的一处（有意）**：`OrderFlow` 的"施放结算"（`Resolve` / 100）与 `PlayManager` 的"打出宣告"
> （`Resolve` / 0）之间也是数字差。但两者**注册者不同**（内核 flow 在自己的构造期注册，游戏层 manager 在
> 对局装配期注册）——新模型里"声明序"只在**同一 owner 的声明表**内有定义，跨 owner 的相对次序没有声明序可依。
> 同 priority 会让早注册的"施放结算"反而先跑（宣告点必须在前）。因此这一处只能靠**拆成员**（把宣告点/反制
> 拆成独立成员，再用成员序表达）解决，属 U5 的裁决点（§A3.1 第 2 条），本轮不动。

**逐组全量（每完成一组一次，均为全量 `/0/0`）**

| 组 | artifacts | 全量 | 增量 |
| --- | --- | --- | --- |
| （起点） | `test-s13-u4-baseline` | 2570 / 0 / 0 | U3 终态复验 |
| hand.add | `test-s13-u4-handadd` | **2573** / 0 / 0 | +3 |
| draw | `test-s13-u4-draw` | **2576** / 0 / 0 | +3 |
| death | `test-s13-u4-death` | **2579** / 0 / 0 | +3 |
| move | `test-s13-u4-move2`（首次 `-move` 有 1 次 Server 抖动，见下） | **2582** / 0 / 0 | +3 |
| join | `test-s13-u4-join` | **2585** / 0 / 0 | +3 |
| damage | `test-s13-u4-damage2`（首次 `-damage` 编译红：`DamagePipeline.EffectAsync` 返回 `bool`，改用 `.Action`） | **2588** / 0 / 0 | +3 |
| order（终态） | `test-s13-u4-order` | **2591** / 0 / 0 | +3 |

测试项目分布（终态）：Orc.Game 1428→**1449**、Orc.Tests 471、Orc.Packages 369、Orc.Mcp 118、
Orc.Server 112、Orc.Script 39、Orc.Manager 20、Orc.Ai 13。**删除的测试：0 条；新增 21 条。**

**7 组 × 3 条等价证据（`tests/Orc.Game.Tests/ActionEdgeOrderTests.cs`）**

| 组 | 段次序 | `ctx.Stop()` | 结果位 |
| --- | --- | --- | --- |
| hand.add | `加入手牌_段次序_前置先于外来挂载边_自有边last收口` | `加入手牌_Stop语义_门禁命中后后续边与after不执行_且before写入保留` | `加入手牌_结果位_入列表Applied且重复加入TargetInvalid` |
| draw | `抽牌_段次序_前置先于外来挂载边_自有边last收口` | `抽牌_Stop语义_门禁命中后后续边与after不执行_且before写入保留` | `抽牌_结果位_Applied且离组入列表` |
| death | `死亡_段次序_前置先于外来挂载边_自有边last收口` | `死亡_Stop语义_门禁命中后后续边与after不执行_且before写入保留` | `死亡_结果位_Applied且死亡链落地` |
| move | `移动_段次序_前置先于外来挂载边_自有边last收口` | `移动_Stop语义_门禁命中后后续边与after不执行_且before写入保留` | `移动_结果位_Applied且落位目标阵线` |
| join | `加入战场_段次序_前置先于外来挂载边_自有边按声明序收口` | `加入战场_Stop语义_门禁命中后后续边与after不执行_且before写入保留` | `加入战场_结果位_Applied且部署落地` |
| damage | `伤害_段次序_前置先于外来挂载边_自有边last收口` | `伤害_Stop语义_免疫命中后后续边与after不执行_且before写入保留` | `伤害_结果位_Applied且扣减防御` |
| order | `指令_段次序_前置先于外来挂载边_自有边last收口` | `指令_Stop语义_反制命中后后续边与after不执行_且before写入保留` | `指令_结果位_Applied且扣费离手` |

- **段次序**：被测成员的名字与位次**直接取自** `EngineProcesses.Members`（不是测试自造），
  断言 `before（前置）→ 外来甲、外来乙（追加序）→ 自有边（声明序，最后）→ after`；
  join 组额外钉住 D2（`部署词条 → 转发` 的声明序）。
- **Stop**：门禁在 `before` 命中 `Stop` 后，同成员的**外来边／自有边／after 全部不执行**，
  且门禁读到前置写入的 `amount=7`（**已执行 before 的写入保留**）。
- **结果位**：在**旧执行面**（生产路径未切换）上调用真实动作，逐组断言
  `ActionOutcomeKind`／`ActionFailureKind` 与迁移前一致（order 另断 `CardPlayed → PointChanged` 与扣费离手）。

**本轮测试改动（逐条改写理由；无删除）**

| 用例 | 改动 | 理由 |
| --- | --- | --- |
| `EngineEntityTests.ActionPipelineMembers_AreDeclared_WithOwnEdgePositionByKind` | 15 条分组断言（8 first／7 last）→ **21 条逐条** + 表/成员表/端清单三方同源 + 非动作成员仍 first | U1 判据是"端在动作里的位置"，U4 判据是"自有边是否末步"；13 个触发端的转发（旧 Commit）本就是末步，故由 first 改 last。条数与同源校验只增不减 |
| `P3ActionFamilyTests.Deploy_Keywords_Run_In_Commit_Head_Before_Unitization` | `commit[0].Priority < commit[1].Priority` → `Equal` | D2：次序改由声明序承担，"谁先谁后"的断言原样保留；删掉的是"用数字差表达次序"的旧写法 |
| `ActionEdgeOrderTests` 的 7 条结果位用例 | 追加 `AppliedAmount`（及 `Reason`／死亡组的默认 `Disposition`）断言 | 任务书的"结果位"含 `Result/Disposition` 两面，原 21 条只断 `Outcome`／`Reason`：实扣量与默认处置零覆盖。断言值直接取自处理端写入（`MarkApplied(1／dealt／0)`、`Disposition=null`），不是测试自造 |

**复核（第二轮，2026-10-26）**：`--artifacts-path artifacts/test-u4-final` ＝ **2591 / 0 / 0**
（Orc.Game 1449、Orc.Tests 471、Orc.Packages 369、Orc.Mcp 118、Orc.Server 112、Orc.Script 39、Orc.Manager 20、Orc.Ai 13）。
测试**条数不变**（本轮只加断言、不加用例）；`new Trigger<` 38 处／17 文件、`ActionEnds.` src 7 ＋ tests 24、
band 引用 46 处／9 文件、`BandValue` 19 行——与 §A3.1 计数逐项一致。

**被证伪 / 新确认的假设**

1. **"21 端可以按语义二分成 first／last 两组"**：**部分证伪**——21 端的自有边都收到"转发／落地／收尾"上，
   逐条核对后 **21/21 都是 `last`**，`first` 组为空集。原因不是漏标：旧 `Initiate`／`Announce`／`CounterGuard`
   是**段内相位**而非独立端，新模型里归 `before` 相位。要得到一个 `first` 动作端，必须**拆成员**
   （把门禁拆成独立成员）——那是结构改动，不在"位次落地"范围内，本轮未做。
2. **"外来挂载边里的 `ctx.Stop()` 会终止宿主成员"**：**证伪**。`RunEdgesAsync` 把一条外来边当作一次
   **嵌套成员执行**，`Stop` 落在嵌套成员的 `MemberExecutionScope` 上，宿主成员继续跑
   （`WrapChannelTests.停止作用域限于当前成员不外溢到调用方其它成员` 正是这条的守护）。
   因此动作门禁在新模型里的承载形态必须是 **`before` + `Stop`**（R1i 迁移规则），U4 的 Stop 证据按此写。
   若将来要求"外来边 Stop 也终止宿主成员"，需改 `RunEdgesAsync`（把 `result.Stopped` 回写宿主 `scope`）
   并改写上述既有断言——**属语义裁决**，本轮未擅改。
3. **`DamagePipeline.EffectAsync` 返回 `bool`**（门面压平了 `ActionResult`）：结果位断言必须
   `pipeline.Action.EffectAsync(...)` 取动作实例，否则拿不到 `Outcome`／`Reason`。已按此写。

**本轮风险 / 抖动**

- `Orc.Server.Tests` 出现 1 次失败（`-move` 那次全量）：单项目复跑 **112/0**、同组全量复跑（`-move2`）**绿**。
  与既有 `Orc.Mcp.Tests` flaky 同类（端口／进程竞争），非本轮引入。

#### §A3.1 交给 U5 的剩余工作量（实测计数，2026-10-26 HEAD 工作区）

| 类 | 落点 | 计数 | 性质 |
| --- | --- | --- | --- |
| band 挂接点（生产） | `KeywordEffects.cs:539`（重甲 `Initiate`）／`:597`（免疫 `Rewrite`）；`CommandManager.cs:266`（死亡 `Rewrite`，U3 起经 `ActionEnds` 取实例） | **3** 处 | R1i 明定改为 `wrap before`；前两处依赖 A3（词条→组件实体） |
| band 自注册（动作实现） | `src/Orc.Foundation.Actions/*` 内 `Register(..., band, ...)`：JoinBattlefield 13、Draw 5、Death 5、Damage 5、Move 3、Attack 3、AddToHand 3 | **37** 处 | 自有边改为 `OwnedEdge` 后逐条删 band 形参；D2 已把 join 的相对次序改成声明序 |
| band 自注册（流程） | `PlayManager.cs` 的指令宣告（`Resolve, 0`）／收尾（`Finalize, 100`）——用的是**另一个枚举** `OrderFlowBands` | **2** 处 | `order.cast` 的"宣告点 vs 施放结算"是否需要拆成员（当前同端两条事件，新模型只能靠 before/after） |
| 旧执行面实例 | `ActionEnds` 消费点 src 7 ＋ tests 24 | **31** 处 | 删 `ActionEnds` 前必须清零 |
| 旧类型实例化 | `new Trigger<` src **38** 处 / 17 文件 | 38 | 动作 17（6 文件）、`OrderFlow` 2、其余 19 |
| 旧类型引用 | `Trigger<` src 185／tests 219；`Bus` tests 123 | 404+ | 随 A4/A5 同批 |
| 测试断言改写 | `BandValue` **19 行 / 3 文件**（P1／P2／P3） | 19 | U4 已把"位次"证据前移为边序断言（`ActionEdgeOrderTests`），这 19 行可直接删或换成边序断言 |
| 显示名取件 | `Match.cs` 四个显示名工厂 ＋ `MatchNamedTriggers` ＋ `LegacyDisplayNames` | 1 组 | 随 A2/A4 |

**U5 的三个裁决点（不裁决就做不成等价）**

1. **外来边 `Stop` 是否上抛到宿主成员**（见上"被证伪 2"）：不上抛 ⇒ 门禁一律 `before`；上抛 ⇒ 改
   `RunEdgesAsync` 并改写 `WrapChannelTests` 既有断言。
2. **`order.cast` 的 Counter／Resolve 两相位**：一个成员只有 `before` 与自有边两段，反制（Counter）与
   宣告点（Resolve 0）都在施放结算之前，只能都归 `before`（先后按声明序）——需确认"反制 → 宣告 → 结算"
   的三段语义在 FIFO 下仍成立。
3. **band 枚举删除顺序**：先删动作实现的 37 处（可机械），再删 3 个挂接点（依赖 A3），最后才删
   `ActionTriggerBands`／`ActionHandleBands` 类型与 19 行 `BandValue` 断言。

### §A4（本轮）U5-a：band 消费者下线 ＋ 生产装载切 v4 ＋ 卡牌↔实体桥

**终局全量（`--artifacts-path artifacts/test-u5a-full1`；终态复跑 `artifacts/test-u5a-final2` 同数）：2602 通过 / 0 失败 / 0 跳过**
（Orc.Game 1449→**1457**、Orc.Tests 471、Orc.Packages 369→**372**、Orc.Mcp 118、Orc.Server 112、
Orc.Script 39、Orc.Manager 20、Orc.Ai 13）。基线 2591 → **+11 条新测试、0 删除、0 跳过**。

#### §A4.1 band 退出的表达（不是重命名）

新增 `TriggerEdgePosition`（`src/Orc/Core/Trigger.cs`）：**外来挂载边 → 自有边 → 外来尾边**——
`Mounted`／`Owned`／`MountedAfter`；排序键＝`位次×10^9 ＋ band 值×10^3 ＋ priority`，
旧注册面（带 `band` 的 `Register`）位次恒 `Mounted` ⇒ **既有触发器逐项排序不变**（`EdgePositionMigrationTests.旧band触发器_排序与改前一致_全为外来边位次` 守护）。

| 面 | 改前 | 改后 |
| --- | --- | --- |
| 动作自有边（7 个动作实现，37 处） | `Register(..., ActionTriggerBands.Commit/Apply/Initiate, 100)`＋构造期 `bandType: typeof(...)` | `RegisterOwned(name, handler, 100)`；**构造期不再声明 band 方案** |
| 重甲（`KeywordEffects.cs:539`） | `AttachTo(pipeline.CombatDamage, …, Initiate)` | `AttachTo(...)` ＝ `RegisterMounted`（外来边在自有转发之前） |
| 免疫（`KeywordEffects.cs:597`） | `AttachTo(pipeline.Apply, …, Rewrite)` | 同上（外来边在自有扣数值之前） |
| 指挥死亡改写（`CommandManager.cs:266`） | `Register(..., ActionHandleBands.Rewrite, priority)` | `RegisterMounted(...)`（外来边在免死守卫／死亡链之前） |
| `injects` 的 `band` 名 | 解析成枚举成员后按数值排序 | 只决定位次：`Commit` → `MountedAfter`；`Apply`／`Rewrite`／`Veto`／`Initiate`… → `Mounted`（R1i 迁移规则原文） |
| 目标触发器仍声明专门 band 方案（内核 flow） | — | 沿用按名解析（过渡兼容；U5-b 随 flow 触发器一起收） |

**为什么这是"切消费"而不是"改名字"**：调用点不再出现段名，也不再需要构造期声明 band 方案；
`ActionBands.cs` 的两个枚举**只剩定义**（`src/` 内消费者 0），位次由注册面本身表达。

#### §A4.2 验收数字（可复跑）

```text
grep -rn "ActionTriggerBands\.\|ActionHandleBands\." src/   → 0
grep -rn "ActionTriggerBands\.\|ActionHandleBands\." tests/ → 0（改前 44）
grep -rn "BandValue" tests/ --include=*.cs                  → 0（改前 19 行 / 3 文件）
grep -rn "new Trigger<" src/ | wc -l                        → 38（17 文件；U5-a 不减少，属 U5-b）
grep -rn "Trigger<" src/ ...                                 → 187；tests → 221
```

#### §A4.3 测试改动（逐条：改写而非删除；断言强度只增不减）

| 用例／文件 | 改动 | 理由（等价性） |
| --- | --- | --- |
| `P1ActionContractTests`（5 行） | `BandValue == Commit` 的"有且仅有一条转发"＋4 条"空段不存在" → `Assert.Single(events)`＋名称＋`Position == Owned` | "空段"是 band 概念；等价断言是"事件集恰一条转发"（更强：任何多余事件都会红） |
| `P1ActionContractTests`（2 行） | 处理端 `BandValue == Apply` → 名称＋`Position == Owned`；`Rewrite` 注册 → `RegisterMounted` | 同一注册项、同一执行点、同一 priority |
| `P2ActionFamilyTests`（移动/加入手牌/死亡槽，5 处） | `Commit`／`Apply`／`Rewrite`／`Veto` 的段断言 → **完整事件名序列＋位次** | 段→位次的同义改写；死亡槽新增"外来边在两条自有边之前"的次序断言 |
| `P3ActionFamilyTests`（13 处） | `BandType == typeof(...)` → `Assert.Null`；`Commit` 段过滤 → `Position == Owned` 过滤（并断言完整 4 条自有边）；探针注册 → `RegisterMounted` | band 方案退役后"声明了专门枚举"不再成立；D2 的"词条先于转发"仍逐字保留（声明序） |
| `DamagePipelineTests`（4 处） | `Initiate`／`Rewrite` 注入 → `RegisterMounted` | 改写仍在默认结算之前（断言"处理端读到改写后的值"未变） |
| `EffectSystemTests`／`TriggerCardTests`／`G14HistoryCountTests`（5 处） | `Initiate`／`Apply` → `RegisterMounted`；`Apply, 200` → `RegisterMountedAfter` | 前者的旧段在自有边之前，后者在之后（`Apply 200 > 100`），位次逐一对应 |
| `KeywordProgramTests`／`ActionContractWideningTests`（5 处） | `Rewrite` → `RegisterMounted`；`Commit, 50` → `RegisterMounted`（外来边在前） | 旧 `Commit` 段 priority 50 在自有转发之前，按位次改写后次序不变 |
| **新增** `EdgePositionMigrationTests`（4 条） | 旧面排序不变／三位次排序／重甲+免疫位次与零伤害／死亡改写位次序列 | 三个 band 消费者的**直接等价证据**（不依赖 band 数值） |
| **删除的测试：0 条；跳过的测试：0 条。** |

#### §A4.4 卡牌 ↔ 编排实体桥（批次 3 遗留）

- `CardBase.EnsureModelEntity()`（加载期）：`engine.Model.CreateEntity(CardTypeBinder.PrefabIdFor(host), Name#MatchCardId)`，
  `entity.Bound = this`；`ModelEntity` 公开只读面。无编排运行时／无宿主种类＝**不建实体**（旧链照常）。
  实体名带对局内卡 id（`Name#id`），避免同名卡在审查面按名建节点时互相顶掉。
- `CardBase.LoadModelComponents()`：卡数据 `effects.prefabs` 逐条按**声明序**装配（A-3：一条声明＝一个组件实例）；
  运行时目录没有该 id（旧格式包）＝跳过（旧效果链承载）；单个组件失败＝隔离留痕、不阻断。
- `CardBase.Destroy()` 覆写：先 `entity.Bound = null; entity.Destroy()`（卸载组件 → 撤销该实体创建的全部边/包装 →
  从运行时移除），再走既有卡销毁链。
- 测试 `CardModelBridgeTests`（4 条）：实体存在且 `Bound` 为卡本身／成员 `InvokeAsync(play)` 可执行／
  销毁后实体移除且解绑／**sample `diy.adjacent-cover` 的两个 v4 组件按声明序装配到卡实体**。
- 未覆盖：`Hq`（不是 `CardBase`；HQ 的实体化属后续）。语义边界（每实体一承载对象／销毁即清 `Bound`）按 §七 第 6 条确认口径实现。

#### §A4.5 生产装载切 effects-v4

- `PackageScanner.ComponentsDirectoryName = "effects-v4"`；`PackageScan`／`CardPackContent` 新增
  `ComponentsDirectory`（`PackageRegistrar` 透传；构造函数末位可选参数，程序化夹具零改动）。
- `CardPackAssembler`：包提供 v4 ⇒ **只装 v4**——`AssetPackLoader.Load(engine.Model, …)` 原子装载
  （全量校验＋暂存＋统一注册），旧 `effects/` **不再进入装载路径**；v4 包的 `DeclarePrefab` 不再进旧效果链
  （声明由卡牌↔实体桥装配）。只提供旧格式的包＝过渡兼容路径不变。
- 测试 `CardPackV4LoadTests`（3 条）：扫描识别 v4 目录／有 v4 时组件进编排运行时且**旧目录放坏文件也不影响装载**
  （反证"未读取旧路径"）／v4 坏资产＝必需包上抛、运行时零注册。
- **边界（必须知道）**：生产的**信号投递**仍走旧总线（A2 未做），因此 v4 组件目前只完成"装载＋装配＋成员可执行"，
  "信号驱动执行"要等 A2。当前 `CardPackAssembler` 只被测试调用（生产 `McpService.MatchLaunch` 走
  `assembly.EffectDirectories`），所以本步没有"生产对局行为切换"的风险，但也**不能**宣称生产玩法已由 v4 承载。

#### §A4.6 被证伪的假设：`Match.cs` 4 个显示名工厂不可在本步删除

**假设**（任务书）：v4 装载后新路径按成员地址取件，4 个显示名工厂（`伤害处理触发器`／`对战伤害触发器`／
`效果伤害触发器`／`战斗伤害触发器`）可删。

**实测证伪**：删除后 `DiyExample09_AdjacentCoverTests` 3 条中 **2 条红**（"3 伤害实扣 2"变成实扣 3；
叠加用例 expected 4 / actual 2）——原因是**旧格式资产仍由测试直接装载**（`DiyExampleKit` 走
`Engine.Prefabs.LoadDirectory(effects/)`），其 `injects.target` 用中文显示名定位。
删掉工厂＝这些注入静默失效；改写这些用例去断言"减伤不生效"就是硬约束 2 明令禁止的"删测试换绿"。

**结论**：4 个工厂保留到"旧格式资产不再被任何执行路径装载"（＝A2＋A3 完成）之后，与 `MatchNamedTriggers`／
`LegacyDisplayNames` 同批删除。证据命令：`samples/card-pack-diy/effects/diy.adjacent-cover.mitigation.prefab.json:28`
（`injects.target = 伤害处理触发器`）。

#### §A4.7 U5-a 改变了哪些行为（R8）

1. **动作端的 band 方案不再声明**：`Trigger.BandType` 对动作端为 `null`；以 band 名注入的资产生效路径改为"位次映射"。
   影响面＝审计/诊断读 `BandType` 的地方（仅测试，已改）。
2. **卡的模型实体**：每张 `CardBase` 子类实例加载后拥有一个编排实体；审查面 `entities` 段从此包含卡实体。
   纯加性（旧执行面不变）。
3. **v4 组件装配**：包内有 `effects-v4` 时，卡加载会额外装配组件实体（失败隔离、不影响旧效果）。
4. **`TriggerEventInfo.Position`**：新增只读面（加性）。

#### §A4.8 本轮风险 / 取舍

1. **旧 band 名 → 位次的映射是一张固定表**（`Effect.cs:IsAfterOwnedBandName`）：只有 `Commit` 映射到尾侧，
   其余前置。历史资产若用 `Commit` + 低 priority 表达"结算前观测"（测试里有 2 处，已改为 `RegisterMounted`），
   语义会反转。**样本资产中不存在该用法**（`grep band samples/*/effects`：只 4 处 `Rewrite` + 1 处 `Commit 200`）。
2. **旧面与新面同触发器内混用**：位次恒优先于 band；旧面（`band` 注册）与新面（`RegisterOwned`）混在同一触发器上时，
   所有旧面项一律排在自有边之前——这是"外来边"的定义，不再是"按数字插入"。
3. **`CardPackAssembler` 的 v4 分支依赖 `engine.Model`**：未装配时回退 `GameEnginePrefabs.CreateRuntime()`
   （只对独立测试有意义；`Match` 恒已装配）。
4. **v4 组件装配容错**：单组件失败只留痕不阻断卡加载（保证旧链不被拖红）；若要 fail-fast，需等 A3 正式切换。

### §B S14 `CollectLegacy*` 清理
`src/Orc/Core/Model/AuditGraphBuilder.cs:153-291`（约 140 行）把旧总线订阅与 `injects` 投影成审查边。
它投影的对象（`Bus`／`injects`）仍在生产使用；删除会让审查面**能力退化**（R7 禁止）。
随 §A2/A4 一并删除，不单独做。

### §C 作者文档中旧 syntax 的**彻底**退场
本批以"09 章 + 术语提醒 + 04 章口径"落地。01–08 章仍按旧 schema 描述**当前生产装载格式**——
在生产切到 v4（§A3）之前，把它们改成 v4 会让作者写出"生产读不了"的资产。
因此文档的"旧语法清零"与 §A3 同批完成（本批不做，属有意为之）。

## 七、重构完成后仍需人工确认的事项

1. **A-1…A-8 八条假设一次性审**（尤其 A-2「挂载/包装混用只看声明形态」、A-4「审查导出沿用 AuditChain 形态并升 2」）。
2. **随机选择器的生产可用性**：`AllowRandomTargeting` 缺省 false 意味着**现有含 `sel=random` 的卡包在生产 play 里会报错**
   （样本：`card-pack-diy/diy.volley`、`diy.fire-support` 的 `sel=one` 也需要前端）。
   需确认：这些卡是否属于"简单 AI／测试"用途、还是需要按包/按卡决定是否允许随机。
3. **`sel=one` 的 UI 体验**：效果发起的选靶现在会挂起等前端；需确认人类 UI 已能应答 `action=effect` 的待决请求
   （MCP 侧已支持 `target.submit`／`target.cancel`／`target.claim`）。
4. **审查导出 `schemaVersion=2` + `packages[]`**：前端需增读 `packages[]`（加性，不破坏既有键）；建议在诊断面板展示 `skipped`。
5. **契约基线更新**：`contract-freeze.json` 已重生成（仅删 `BeginTargetSelectionAsync`），提交前请人工复核 diff。
6. **`OrchestrationEntity.Bound` 的语义边界**：本批只用于判定器包装器宿主回填；§A1 会正式把它作为"卡牌↔实体"绑定，
   届时需确认"一个实体只承载一个对象""销毁后 Bound 是否清空"两条语义。
7. **常驻 `Orc.Manager` 锁 DLL**：所有 build/test 必须带 `--artifacts-path`（本批全部如此），不要杀进程。
8. **审查导出的事件字段是破坏性替换**（U5-b）：`events[].band`（数值段号）已删除，改为 `events[].position`
   （`mounted`／`owned`／`mountedAfter`）。仓库内无前端读取，但**外部前端/日志回放工具**若存了旧字段，需要一次性迁移。
   已写入 `docs/前端适配-后端变更-20261020.md` §11；`schemaVersion` 未升（字段替换，不构成结构升级）——若前端要求
   "破坏性变更必须升版本"，这里需要改判。
9. **旧格式资产（`effects/*.prefab.json`）的长期定位**：`PrefabManager`／`PrefabJson`／`EffectPrefab` 等类型的删除
   **不只看 A2/A3**，还要回答"作者的历史资产是否继续可读"。编辑器（`AssetEditorSession`）、迁移工具
   （`AssetMigrationTool`）、MCP 资产工具都依赖这套 DTO；若决定保留向后兼容，则这些类型**永远不删**，
   §十五 的 b5 需要相应缩小范围（只删执行面，不删 DTO）。
10. **`OrderFlowBands` 的两相位**（延续 §A3／§14.3）：`Counter`／`Resolve`／`Finalize` 目前仍靠 band 数值排序。
    本轮删掉了公开 `BandValue`，但内部排序槽保留。若要把 `OrderFlowBands` 也退役，必须先裁决
    "宣告点 vs 结算点是否拆成两个成员"（拆了会改变审计图形状）。
11. **b5 的测试策略**：`Trigger<TView>` 退役会带走 34 个测试文件里的 174 处实例化（机制自身的验收套件）。
    需要人工确认：这些套件是**迁到新模型上重写**（工作量大但保能力），还是**随旧机制一并退役**（能力退化，需 R7 例外）。
    本轮按"不删测试"处理。
12. **`Match.cs` 4 个显示名工厂**：§A4.6 的结论在本轮复核后**仍成立**——只有 `DiyExample09` 改用 v4 路径
    （且 v4 能执行）之后才可删。不要在 A2 之前重试。

## 八、阻塞 / 风险

- **U4 发现（需裁决）**：外来挂载边内部的 `ctx.Stop()` **不向宿主成员外溢**（`RunEdgesAsync` 以"边＝一次嵌套
  成员执行"为单位）。动作门禁因此必须以 `before` + `Stop` 承载（R1i 迁移规则）。若要求"外来边 Stop 也终止
  宿主成员"，需改 `RunEdgesAsync` 并改写 `WrapChannelTests` 的既有断言——见 §A3「被证伪 2」。
- **U4 发现（范围）**：动作端点里不存在"整端就是门禁"的成员，21 端 **全为 `last`**；`first` 组要出现只能
  拆成员（R1i「需要中间插入就拆成员」）。本轮按"末步语义"标注，未做拆分。
- `Orc.Mcp.Tests` 历史端口/进程竞争抖动：本批 118 条一次通过；若复发按既有 flaky 处理。
- `Orc.Server.Tests` 同性质抖动（U4 出现 1 次，单项目复跑 112/0）。
- `Effect.SelectAsync` 的异常在 handler 内会被事件隔离记录并继续（不中断整局）：生产随机/无前端场景表现为
  "该效果不生效 + 事件流 Error 条目"，**不是对局崩溃**。这是既有异常隔离语义（未改），但会影响"为什么不生效"的排查路径——
  已在 09 章 §5 写明。
- 判定器包装器目前在**一个 `Invoke` 里执行两条来源**（新面 + 旧域桶）。R1i 的"单一注册面"要到 `Effect` 退役才完全成立。
  **本批（U0/D3）起两面相位语义不再相同**：新面 `before` ＝ FIFO，旧域桶（`ServiceEntry.SnapshotBefore`）仍是历史 LIFO。
  影响面＝只有"同一个判定器上既被组件资产 `judge-wraps` 又被插件 `WrapBefore` 包住"的场景（当前无生产用例）；
  **跨两面的相对次序不构成契约**，随 `Trigger` 在 U5／A4 退役收敛为单一 FIFO。需要人工确认是否接受这段过渡期差异。
- `TriggerRegistry` 在 U5 前**同时持有两种身份**（成员地址＋触发器实例）。风险：两次登记（一次按地址、一次按实例）
  会各生成一条账；因此动作目录走"一条项双身份"（`Register(trigger, address, layer)`），并有测试钉住"21 端＝21 项、19 个地址"。
- `Order` 动作两端（`engine.OrderFlow.Trigger`／`FinalizeTrigger`）**没有成员地址**：它们是内核 flow 的触发器，
  不是引擎实体成员。U3 必须就"是否升格为 `engine#order.cast`／`engine#order.finalize`"作裁决，否则这两端永远只能按实例登记。

## 九、本批实际执行顺序（复盘用）

1. 盘点旧类型残留面（`new Trigger<`/`Trigger<`/`Bus`/`Effect*` 计数）→ 判定 S13 不可在单批内完成（见 §A）。
2. S9 判定器包装接线：`OrchestrationEntity.Bound` → `JudicatorRegistry.ModelWraps` → `ServiceEntry.Invoke` 接线 →
   `WrapRegistry.RegisterJudge` → `ApplyJudicatorWrappers` 迁到同一注册面 → 6 条新测试。
3. S12／S9 融合：`SelectAsync` 三分派 → 删 `BeginTargetSelectionAsync` → 判定器只做取样 →
   `MatchOptions.AllowRandomTargeting` → 测试夹具扮演前端（`AutoPickBridge`）→ 6 条新测试。
4. 契约基线重生成（差异仅 1 行）。
5. S15 文档：新 09 章 + 6 章横幅 + 04 章口径 + 信号表 + README。
6. R2c 审查面：`RandomSelections` → `AuditChain.RandomSelectors` → JSON/文本段。
7. S14 前端契约：前端适配文档 §10。
8. 全量回归：2544 通过 / 0 失败 / 0 跳过。

## 十、批次 6 执行顺序（复盘用）

1. 读 §A0（阻塞证据＋U1–U5 预案）、requirements R1i（D1–D3 修订）、implementation S2a。
2. 定位模型面：`Members.cs`（成员声明）／`OrchestrationEntity.RunEdgesAsync`（边集合执行序）／`Mounting.cs`（包装注册面）。
3. **U0**：加 `OwnEdgePosition`（默认 First）→ `RunEdgesAsync` 按位次分两段 → `BeforeExecutionOrder` 去反转 →
   改 4 条 LIFO 断言 → 新增 `OwnEdgePositionTests`（3 条）→ 全量 2547/0/0。
4. **U1**：`EngineProcesses` 加 13 常量＋22 项 `MemberNames` → 7 个落地型标 `Last` → 守护测试 9→22 ＋新增位次守护 → 全量 2548/0/0。
5. **U2**：`TriggerRegistry` 加地址身份（3 个登记重载＋`Find`＋地址去重）→ `ActionTriggerEnd.Member`＋标准登记构造 →
   `MatchActionCatalog.End` 去泛型＋19 端填地址 → `MoveAction` 自报两端地址 → 改写/新增注册表断言 → 全量 2549/0/0。
6. 复核 `new Trigger<` 仍 38 处、`I*Action` 零改动、`contract-freeze` 无需重生成（注册面不在冻结清单内）。

## 十一、下一轮（U3）建议

**目标**：6 个 `I*Action` 的两端从 `Trigger<TView>` 换成"成员句柄"，让动作实现自己报成员地址，删掉 U2 在
`MatchActionCatalog` 里的"端→成员名"临时映射。

**建议做法（按可独立验收的小步）**

1. **先定"成员句柄"的类型与语义**（U3 的唯一真问题）：
   - 候选 A：`MemberAddress`（纯地址，值语义、可跨程序集）——与 U2 的注册面同一身份，最小改动；
   - 候选 B：`EngineMemberRef`（地址＋可选实例的句柄对象）——同时满足"按地址登记"与"旧 `Trigger` 实例仍可用"；
   - 建议 A：U2 已把地址变成一等身份，B 只是把过渡期的双身份**对象化**，会推迟 U5。若选 A，`I*Action` 的
     `Trigger<XView> Trigger { get; }` → `MemberAddress Trigger { get; }`，实现内部保留 `Trigger<XView>` 私有字段
     （旧注册面还要用），公开面只暴露地址。
2. **`Order` 动作的归属先裁决**：`order.cast`／`order.finalize` 是否升为引擎成员（`EngineProcesses`）。
   升格＝要新增 2 个成员名＋守护测试，并能补齐 U2 剩下的 2/21 端；不升格＝`IOrderAction` 保留 flow 触发器，
   契约加宽对它例外（需在 R1i 上留一条"内核 flow 例外"记录）。
3. **逐动作迁移顺序**（依赖图反序，先叶子）：`hand.add` → `draw` → `death` → `move` → `join` → `damage` → `attack`。
   每个动作：改实现（两端的公开面）→ 改 `MatchActionCatalog.End` 去掉成员名参数 → 跑该动作的 `P*ActionFamilyTests`。
4. **同批要动的两个消费者**：`EffectRuntime.Board`（`JoinBattlefield` 两端）与 `DamagePipeline`（`Damage` 两端）；
   `KeywordEffects:539/597` 的 band 挂接**留到 U5**（依赖 A3），不要顺手改。
5. **验收**：`grep -rn "Trigger<" src/Orc.Foundation.Actions` 只应剩成员内部的私有字段；
   `MatchActionCatalog` 不再出现 `EngineProcesses.*` 作为"端→成员名"映射；全量绿；
   `TriggerRegistryTests` 的 19/21 变为 21/21（若采纳第 2 条升格）。

## 十二、批次 6 U3 复盘 ＋ U4/U5 交接

### 12.1 实际执行顺序（可复跑）

1. 读 PROGRESS §A0/§八/§十一、requirements R1i、implementation S13 → 确认"契约加宽＝给地址、实例留内部"的口径。
2. 全量基线：`--artifacts-path artifacts/test-s13-u3-baseline` ＝ **2549 / 0 / 0**（与 U2 记录一致）。
3. 落 `ActionEnds`（地址→实例的唯一解析桥）＋ 统一 `Ends` 形态 → 按依赖反序逐动作：
   `hand.add`（2552）→ `draw`（2555）→ `death`（2558）→ `move`（2560，1 次已知 flaky）→ `join`（2564）→ `damage`（2567）→ `order`（2570）。
   每步：改契约＋实现＋目录 → 定向跑该动作族 → **全量跑**（`--artifacts-path artifacts/test-s13-u3-<动作>`）。
4. 复核：`new Trigger<` 38→**38**（U3 不减少，见 §A2.4）；`contract-freeze.json` 无需重生成；
   `KeywordEffects.cs` 零改动（`git diff --stat` 可验）。

### 12.2 本轮风险（新发现，均非阻塞）

1. **契约加宽是插件边界的破坏性变更**（R17 明定 `I*Action` 是替换边界）：仓库内只有 `FoundationActions.Set` 一个实现集，已同批更新；
   若有仓外插件实现这 7 个接口，需按新形态自报 `Ends`。**需人工确认是否在发布说明里点名**。
2. **`ActionEnds` 是新增的过渡桥**：它让"按地址反查实例"合法化，但也让"地址是唯一身份"这条 R1d 原则在过渡期内**可被绕过**。
   约束方式：只允许宿主/门面/测试夹具调用（`ActionEnds` 的文档写明 U5 删除）；U5 时应能一行 grep 出全部调用点。
3. **`KeywordLoadContext.DamageAction` 的名字与类型不再字面一致**（名字仍叫 Action，类型已是管道门面）。
   这是为"完全不碰 `KeywordEffects`"付出的代价；U5 前若做 A3，建议同批改名（`DamagePipeline`）并改那 2 行调用。
4. **指令成员名的归属**：`EngineProcesses.OrderCast/OrderFinalize`（游戏层）是唯一常量源，而 `OrderFlow` 在**内核**（`src/Orc`）——
   内核不认识这两个名字，靠 `OrderAction`（插件程序集，引用游戏层）自报。这与其它 6 个动作"实现在插件、地址常量在游戏层"一致；
   但若将来内核要自己发射 `engine#order.cast`，需把常量下沉到内核（同 `KernelSignals` 的做法）。
5. `Orc.Mcp.Tests` 本轮再次出现 1 次 flaky（单项目复跑 118/0），与 §八 既有记录一致，非本轮引入。

### 12.3 给 U4 的交接（按优先级）

| 步 | 内容 | 验收 |
| --- | --- | --- |
| U4-a | `IAttackAction` 两端并进 `Ends`（`UnitAttackTrigger`／`AttackDamageTrigger` 由 `CommandManager` 建本体后注入实现），删目录里最后一条 `EngineProcesses.*` 映射 | `MatchActionCatalog` 零 `EngineProcesses`；21/21 端全由实现自报 |
| U4-b | D1 位次落地：为"转发（旧 `Commit`）／落地（旧 `Apply`）"确定成员位次形态（`OwnEdgePosition` 已就位，缺的是边装配真源） | 每动作 ≥1 条**边序**断言（不依赖 band 值） |
| U4-c | D2/D3：同段多点相对次序（部署词条 50 前于转发 100）在成员内以声明序表达 | `P3ActionFamilyTests.Deploy_Keywords_…` 改写为声明序断言 |
| U4-d | 本轮 21 条等价测试中依赖 `BandValue` 的断言迁到边序口径 | 全绿；band 相关断言只剩 U5 待删项 |

**U5 的入口**（本轮已把路铺到这一步）：`grep -rn "ActionEnds\." src tests` ＝ 实例面的全部消费点；
`grep -rn "ActionTriggerEnd.Trigger\|entry.Trigger" src tests` ＝ 注册面的双身份残留。这两处清零 = 旧类型可删。

## 十三、批次 6 U4 复盘 ＋ U5 交接

### 13.1 实际执行顺序（可复跑）

1. 读 §A0（D1–D3 与 U4 预案）、requirements R1i（`own-edge-position`／before FIFO）→ 确认判据是
   "**自有边是否链上末步**"，而不是"端在动作里的位置"。
2. 全量基线 `--artifacts-path artifacts/test-s13-u4-baseline` ＝ **2570 / 0 / 0**（与 U3 记录一致）。
3. `EngineProcesses` 建 `ActionEndPositions`（21 端：角色／旧末步段／位次／判据）→ `Build()` 改
   `Act()` 按表取位次（13 个触发端由 `first` 改 `last`）→ 守护测试 15→**21** 条逐条比对。
4. D2：`JoinBattlefieldAction` 删 `DeployKeywordPriority=50`（词条与转发同 priority，声明序定先后）；
   `P3ActionFamilyTests` 的 `Priority <` 改 `Equal`。
5. 新 `ActionEdgeOrderTests`（7 组 × 3 条）→ 逐组补 3 条 → **每组一次全量**：
   2573 → 2576 → 2579 → 2582 → 2585 → 2588 → **2591**（全部 `/0/0`，见 §A3 表）。
6. 复核：`new Trigger<` 38→**38**（U4 不动旧实例化点）；`KeywordEffects.cs` **零改动**；
   `CommandManager.cs:266` 的接入路径未动（U5 范围）。

### 13.2 与 U4 预案的差异

| 预案 | 实际 | 原因 |
| --- | --- | --- |
| U4-a：`IAttackAction` 两端并进 `Ends`（删目录最后一条映射） | **未做**（本轮范围外） | 本轮范围＝位次＋等价＋D2；攻击两端仍由 `MatchActionCatalog.End(...)` 补地址（1 处 `EngineProcesses` 映射） |
| U4-b：每动作 ≥1 条边序断言 | **已做（每组 2 条：段次序＋Stop）** | 见 `ActionEdgeOrderTests` |
| U4-c：D2 声明序 | **已做** | 代码与断言同批 |
| U4-d：U3 那 21 条里的 `BandValue` 断言迁边序 | **未改**（U3 的 21 条不含 `BandValue`；含 `BandValue` 的是 P1/P2/P3 共 19 行） | 那 19 行属 U5（band 下线时一并删/换）；U4 已把位次证据前移到边序，不再叠加 band 依赖 |

### 13.3 给 U5 的入口（现成锚点）

- **位次真源**：`EngineProcesses.ActionEndPositions`（21 条）＋ `EngineEntityTests` 的三方同源守护。
- **边序证据**：`ActionEdgeOrderTests`（21 条）——U5 删 band 时，这 21 条**不需要改**（不依赖 band 值）。
- **实例面清零**：`grep -rn "ActionEnds\." src tests`（31 处）；`grep -rn "entry.Trigger" src tests`。
- **band 清零**：`grep -rn "ActionTriggerBands\.\|ActionHandleBands\." src`（46 处 / 9 文件）；
  `grep -rn "BandValue" tests`（19 行 / 3 文件）。

### 13.4 复核记录（第二轮，2026-10-26）

| 项 | 结果 |
| --- | --- |
| 全量（`artifacts/test-u4-final`） | **2591 / 0 / 0**，分布与 §A3 终态一致（Orc.Game 1449 起） |
| 起点复核 | `artifacts/test-u4-verify` 亦为 2591 / 0 / 0（改动前） |
| 本轮唯一改动 | `ActionEdgeOrderTests` 7 条结果位用例追加 `AppliedAmount`／`Reason`／`Disposition` 断言（**只加不删、条数不变**） |
| 计数复核 | `new Trigger<` 38／17 文件、`ActionEnds.` 7＋24、band 46／9、`BandValue` 19——与 §A3.1 逐项吻合 |
| 旧实例化点 | 未动（U5 范围）；`KeywordEffects.cs:539/597`、`CommandManager.cs:266` 未触碰 |

**待裁决（延续 §八，非本轮可解）**：`first` 组为空集。判据冲突来自两处措辞——
R1i 的 normative 定义是"**流程型**成员（打出／部署／指向）＝`first`，**管道型**成员（伤害处理／动作落地）＝`last`"；
而本轮任务书把 `first` 描述为"前置校验／门禁（旧 `Initiate`/`Announce`/`CounterGuard`）"。21 个动作端**全部是管道型**
（转发 `Commit`／落地 `Apply`／结算 `Resolve`），旧 `Initiate`/`Announce`/`CounterGuard` 是**端内相位**而非独立端，
新模型里由 `before` 承载。若要求 `first` 组非空，唯一做法是把门禁**拆成独立成员**（R1i「需要中间插入就拆成员」）——
那是结构改动，会改变动作端清单与审计图形状，不在 U4「位次落地」范围内。**需要父 agent 明确**：
接受"21/21 last + 门禁走 before"，还是启动拆成员。

## 十四、U5-b 交接清单（U5-a 结束时实测，2026-10-26 HEAD 工作区）

> 口径：本步**没有删任何类型**（`Trigger<TView>`／`Effect`／`Bus`／`TriggerRegistry` 双身份都在），
> 但它们的生产消费者已按下面清单收敛。U5-b 的验收应是"本清单归零后再删类型"。

### 14.1 仍被谁引用（按文件与处数）

> 本表是 **U5-a 结束时**的快照，用于定位阻塞点；U5-b 之后的实际状态（删了什么、残留多少）以 **§十五** 为准。
> 其中 `ActionTriggerBands`／`ActionHandleBands` 已删、`TriggerEventInfo.BandValue` 公开面已删。

| 类 | 落点（处数） | 说明 |
| --- | --- | --- |
| `new Trigger<`（38 / 17 文件） | `CommandManager` 7、`JoinBattlefieldAction` 4、`DamageAction` 4、`DrawAction` 3、`OrderFlow` 2、`Effect` 2、`CardLifecycle` 2、`CardMemberTable` 2、`MoveAction` 2、`DeathAction` 2、`AddToHandAction` 2、`SandboxPrimitives` 1、`Hq` 1、`UnitCard` 1、`RetriggerSystem` 1、`OrderCard` 1、`CounterCard` 1 | U5-a **刻意不动**（动作实现内的私有字段仍是旧执行面的唯一实例源） |
| `Trigger<` 泛型引用 | src **187** 处（`UnitCard` 19、`CommandManager` 16、`DynamicEffect` 14、`AttackAction` 12、`DamageAction` 10…）；tests **221** 处（`TriggerValidationTests` 24、`EffectSystemTests` 17、`TriggerWrapperTests` 14…） | 类型退役的机械面 |
| `TriggerEventInfo.BandValue` | src **14** 处：`Trigger.cs` 12（字段/构造/ToInfo/排序）、`AuditChainJson.cs` 1、`AuditChainText.cs` 1；tests **0** | 审查导出仍输出 `bandValue`——U5-b 要么删字段，要么固定写 0 并标注废弃 |
| `Trigger<TView>.BandType` | src **4**（`Trigger.cs` 1、`DynamicEffect.cs` 2、`Effect.cs` 1）；tests **5**（`P3ActionFamilyTests` 的 `Assert.Null`） | 动作端已全为 `null`；剩余用途＝内核 flow／自定义 band 的按名解析 |
| `ActionTriggerBands`／`ActionHandleBands` | **仅定义**：`src/Orc.Game/Actions/ActionBands.cs`（2 个枚举）；src/tests 消费者 **0** | 可直接删类型（U5-b 第一步） |
| `Register(..., Enum band, ...)`／`RegisterBand` | `Trigger.cs` 1 个重载、`DynamicEffect.cs` 2（`RegisterBand`／`GetBandType`）、`Effect.cs` 1（`ResolveBand`） | 旧面（内核 flow / `OrderFlowBands` 5 处）仍用；跟随 flow 触发器一起收 |
| `ActionEnds` 消费点 | src **7**：`CommandManager` 1、`DamagePipeline` 4、`EffectRuntime.Board` 2；tests **24** | 删 `ActionEnds` 前须为零 |
| `TriggerRegistryEntry.Trigger`（实例身份） | src **2**：`NamedTriggerTable.cs` 1、`TriggerRegistry.cs` 1；tests **14**（`TriggerRegistryTests` 12、`CardLoaderTests` 1、`MatchCardAssemblyTests` 1） | U2 起的双身份；U5-b 只保留 `Member`/`Address` |
| 显示名取件链 | `MatchNamedTriggers` src 5 文件（`Match.cs`、`CardMemberTable.cs`、`Player.cs` ×2、定义）；`LegacyDisplayNames` src 3（全在 `CardMemberTable.cs`）；`TryFindNamedTrigger` src 3（`CardBase`／`EffectSystem`／`Hq`）＋ tests 2 | **阻塞点**：旧格式资产仍由测试直接装载（§A4.6）；须等 A2＋A3 |
| `Bus`／`HookRegistry`／`PrefabManager`／`PrefabJson` | src 40（`Bus`）＋51（另三者）；tests `Bus` **123** | 生产信号投递（A2）未切换，不能删 |
| `Effect*` 观测点 | tests **613 处 / 87 文件** | A5 的机械面（按 S14 第 5 条改断"审查面上的边"） |
| `OrderFlowBands` | src **5**（`PlayManager` 2 处注册 + 定义/读面） | 与 `order.cast` 的两相位裁决（§A3 未裁决项）同批 |

### 14.2 建议的 U5-b 顺序（每步可独立验收）

> **执行状态（2026-10-26）**：b1／b2 **已完成**（公开 band 面归零）；b2（清单项的 `ActionEnds`）／b3／b4／b5 **未做**，
> 阻塞证据与解锁条件见 **§十五**。下表保留原计划，完成项以 §十五 为准。

| 步 | 内容 | 验收 |
| --- | --- | --- |
| b1 | 删 `ActionBands.cs` 两个枚举（零消费者）＋删 `TriggerEventInfo.BandValue`（或固定 0）；`AuditChain` 的 `bandValue` 键随前端契约决定 | 全量绿；`grep BandValue src` = 0 |
| b2 | `ActionEnds` 的 31 处消费改为"端实例由实现持有 + 目录只用地址"（`DamagePipeline`／`EffectRuntime.Board`／`CommandManager`） | `grep "ActionEnds\." src tests` = 0 |
| b3 | `TriggerRegistry` 删实例身份（`Trigger` 字段/`entry.Trigger` 14 处测试断言改地址） | 注册表 21 端全按地址；全量绿 |
| b4 | 显示名取件链：`MatchNamedTriggers`／`LegacyDisplayNames`／`TryFindNamedTrigger`＋`Match.cs` 4 个工厂（**前置：A2 信号投递 + 旧格式资产不再被装载**） | `grep "伤害处理触发器" src` = 0；DiyExample09 改走 v4 路径仍绿 |
| b5 | 删 `Trigger<TView>`／`Effect`／`Bus` 等（`new Trigger<` 38 → 0） | 全量绿 + 契约基线重生成 |

### 14.3 本步遗留的判据缺口（需人工裁决）

1. **`first` 组仍为空集**（延续 §A3）：若启动"门禁拆独立成员"，动作端清单与审计图形状会变。
2. **旧 band 名 → 位次映射表**（`Commit` → 尾侧，其余前置）：当前样本无冲突用法；若未来资产用 `Commit` + 低 priority
   表达"结算前"，语义会反转——建议在 U5-b 把 `injects.band` 整体改为 `phase: before|after`（作者面一次性迁移）。
3. **v4 的"执行"仍缺 A2**：本步只让 v4 进装载/装配/成员可执行；信号驱动仍走旧总线。

---

## 十五、U5-b（本轮）执行记录：band 面归零 ＋ 旧类型删除的硬阻塞证据

> **一句话结论**：本轮把**真正零消费者的叶子**删干净了（2 个类型 + 审查面 1 个公开字段）；
> 清单里其余类型**一个都删不掉**——不是因为"引用多"，而是因为**生产执行仍全部走旧机制**
> （新模型 `EmitAsync` 的生产调用点为 **0**），删任何一个都会让效果静默失效或删掉机制自身的验收套件。
> 该判断与「十四」交接清单的 b4／b5 前置条件（A2／A3）一致，本轮把它变成了可复跑的数字。

### 15.1 本轮真删了什么（附为什么能删）

| # | 删除项 | 落点 | 改前消费者 | 为什么可以删 |
| --- | --- | --- | --- | --- |
| 1 | `ActionTriggerBands` ＋ `ActionHandleBands` | `src/Orc.Game/Actions/ActionBands.cs`（整文件，2 个枚举） | `src` **0**／`tests` **0** | U5-a 已把全部 band 消费者改成位次注册；两枚举在 U5-a 结束时只剩定义（§14.1 已标注） |
| 2 | `TriggerEventInfo.BandValue`（**公开面**） | `src/Orc/Core/Trigger.cs` | `tests` **0**；审查导出 2 处 | 事件节点的 `band` 是新模型里不存在的概念；执行序由 `Events` 数组顺序表达，不依赖该数值 |
| 3 | 审查导出 `events[].band` → `events[].position` | `AuditChainJson.cs`／`AuditChainText.cs` | 前端仓库内 **0** 处读取 | 字段替换而非删除能力：`position` 取值 `mounted`／`owned`／`mountedAfter`，比段号更贴近新语义 |
| 4 | 悬空 XML `cref` 同步 | `CommandManager.cs:360-370`、`AttackAction.cs:11-19/41-42` | — | 三个文件引用了已删枚举；编译不报错（cref 不校验），但会在文档里留下死链 |

**刻意保留的 band 内部面（不是漏删）**：`EventEntry.BandValue`（排序槽）＋ `ResolveBandValue`（校验）＋
`MaxBandValue`，共 **9 处，全在 `Trigger.cs` 内部**。原因：**流程触发器**（`OrderFlow.Trigger` 的
`Counter(1) → Resolve(2)`、`FinalizeTrigger` 的 `Finalize(3)`）仍靠它排序；删掉会改变"反制 → 宣告 → 结算 → 收尾"
的次序（§A3 未裁决项、§14.3 第 2 条）。因此本轮口径是**公开面／审计面归零，内部排序槽保留**。

### 15.2 分批与全量（每批一次全量，均带 `--artifacts-path`）

| 批次 | 内容 | artifacts | 结果 |
| --- | --- | --- | --- |
| 起点 | 未改动（复验 U5-a 终态） | `artifacts/test-u5b-baseline` | **2602 / 0 / 0** |
| b1 | 删 `ActionBands.cs` ＋ 3 处 cref 同步 | `artifacts/test-u5b-b1` | **2602 / 0 / 0** |
| b2 | `TriggerEventInfo.BandValue` 公开面删除 ＋ 审查 `band`→`position` ＋ 2 条导出断言 | `artifacts/test-u5b-b2` | **2602 / 0 / 0** |

分布（b2 终态）：Orc.Game **1457**、Orc.Tests **471**、Orc.Packages **372**、Orc.Mcp **118**、Orc.Server **112**、
Orc.Script **39**、Orc.Manager **20**、Orc.Ai **13**。**条数与基线逐项一致**（只删类型/字段，不动用例）。

### 15.3 测试改动（0 删除、0 跳过、0 新增用例）

| 用例 | 改动 | 理由 |
| --- | --- | --- |
| `AuditChainExportTests.JSON_节点事件与实例_完整表达` | ＋2 条断言：`events[0].position == "mounted"`、`events[0]` **不含** `band` 键 | 钉住 15.1 第 3 条的字段替换；这两条是"旧字段已消失、新字段已生效"的直接证据（只加断言，不加用例） |

**被删测试：0 条**；**被跳过的测试：0 条**；**被改写成弱断言：0 条**。

### 15.4 为什么 b3–b5 删不掉（逐类型：引用面 → 阻塞消费者 → 解锁条件）

**两个总闸门（先看这个，再看逐类型表）**

| 闸门 | 实测证据 | 后果 |
| --- | --- | --- |
| **A2 未做**（信号投递仍在旧总线） | `Match.cs:195-201` 原文："本批**只声明、不接线**：引擎成员没有自有边，生产执行仍走旧总线（`Bus`）"；`grep -rn "\.EmitAsync(" src`（排除唯一实现 `Model/Signals.cs`）＝ **0**；生产发射点 `GameUpdates.Emit*` **36** 处 ＋ `Bus.Emit` **4** 处 | 删 `Bus`／`Updates` ⇒ **全部被动触发器（`hooks`）与更新驱动的效果失效**；删 `Trigger<TView>` ⇒ 连主动动作链也没有执行面（不再有"触发"这回事） |
| **A3 未做**（效果执行仍由 `Effect` 子类承载） | `CardBase.cs:479-482` → `CardEffectLoader.LoadAsync`；`Effect.Injections` 被审查面（`AuditGraphBuilder.cs:229`）与编排采样（`Orchestration.cs:191`）读取；v4 只到"装载＋装配＋成员可执行"（§A4.5） | 删 `Effect`／`EffectPrefab`／`EffectSnapshot` ⇒ 卡包装载链断裂、审查面 injection 段消失 |

| 类型（清单项） | src／tests 引用 | 阻塞消费者（file:line） | 解锁条件 |
| --- | --- | --- | --- |
| `Trigger<TView>` | **186**／**221**；`new Trigger<` **38 处/17 文件**（src）、**174 处/34 文件**（tests） | `CommandManager`（7 处实例化）、`UnitCard`／`OrderCard`／`CounterCard`／`Hq`、6 个动作实现、`OrderFlow`、`Bus.Mount`、`TriggerRegistry` 实例身份、审查导出 | A2＋A3 完成；**且**测试侧 174 处实例化所覆盖的机制套件需有等价替代（见下"为什么不能删测试"） |
| `ITriggerMetadata`／`TriggerKind`／`TriggerEvent<TView>`／`TriggerRegistration` | 34／142／122／63（src+tests 合计） | `Orchestration.Register` 的非泛型身份（`Orchestration.cs:42`）、`Bus.Mount` 的 Passive/hooks 判定（`Bus.cs:37-58`）、注册句柄／撤销（`Effect.cs:184/275`） | 随 `Trigger<TView>` 同批；`TriggerRegistration` 需先由"地址＋边"取代撤销语义 |
| `Effect`／`PassiveEffect`／`ActiveEffect<TView>` | **261**／41／21 | `CardEffectLoader`（装载）、`ActiveEffect : ICastAction`（指令施放）、`Effect.Injections`（审查／编排采样） | **A3**（效果行为由组件锚定）＋ A2（信号驱动）；`ActiveEffect` 另需 `ICastAction` 的执行面迁移 |
| `EffectPrefab`／`EventPrefab`／`TriggerPrefab`／`EffectSnapshot` | 41／97／69／123 | `PrefabManager`（旧包装载）、`PrefabJson`（DTO）、`EffectCompiler`／`EffectTemplate`（文本→效果）、编辑器与迁移工具（`AssetEditorSession`／`AssetMigrationTool`／MCP `AssetTools`） | **A3 之后仍需裁决"旧格式资产是否继续可读"**（作者向后兼容是产品决定，不是代码决定） |
| `DynamicEffect`／`EffectInjection` | 31／6 | 动态效果装载（`EffectRuntime.DynamicEffects`）、审查 injection 段 | 同上（A3） |
| `EffectSystem`（**类型本身已不存在**） | 仅剩文件名 `src/Orc.Game/Cards/EffectSystem.cs` 与注释/测试类名 | 文件内是 `CardEffectRegistry`／`CardEffectLoader`（生产效果装载链） | 该目标**已达成**（`grep "class EffectSystem" src` = 0）；文件名属 A3 收口时的改名项 |
| `Bus` | **40**／**123** | 36 处 `GameUpdates.Emit*`、4 处 `Bus.Emit`、`CardLifecycle.cs:235/236`、`Effect.cs:353`、`RetriggerSystem.cs:92` 挂载被动触发器 | **A2** 完成（`Emit` ⇒ `engine#<信号>` 成员） |
| `HookRegistry`／`Updates` | 9／**377** | `LogicEngine.Hooks`（`LogicEngine.cs:35/61`）、`Updates` 是 4 个更新字符串常量（被 377 处引用） | 随 `Bus` 同批；常量需先并入信号表 |
| `PrefabManager`／`PrefabJson` | 30／93 | `Engine.Prefabs`（`LogicEngine`）、旧格式包装载、`PrefabWriter`、编辑器／迁移／MCP 资产工具 | A3＋"旧格式退役"裁决；**即使旧格式退役，编辑器仍需读历史资产** |
| `MatchNamedTriggers`／`LegacyDisplayNames`／`TryFindNamedTrigger`／`Match.cs` 4 个工厂 | 5／3／5／1 组 | `CardEffectLoader.cs:496`（`injects.target` 按名解析）、`Match.cs:698-708`（8 个按名登记） | §A4.6 已实测：删 4 个工厂 ⇒ `DiyExample09` 2 条红（旧格式资产仍由测试直接装载）。**必须先让旧格式资产不再被任何执行路径装载** |
| `AuditGraphBuilder.CollectLegacy*` | 4 | 投影 `Bus` 订阅与 `injects`（`AuditGraphBuilder.cs:31/153/213/217`） | 随 `Bus`／`injects` 退役；先删＝审查面**能力退化**（R7 禁止，§B） |

**为什么不能"删测试换绿"（对硬约束的正面回答）**

1. `174` 处 `new Trigger<` 分布在 **34 个测试文件**，其中前 6 名是 `TriggerValidationTests`(24)、`TriggerWrapperTests`(13)、
   `TriggerSourceTests`(12)、`TriggerBandOrderingTests`(12)、`TriggerControlTests`(11)、`BusMountTests`(10)
   ——这些**不是"旧类型存在"的断言，而是触发器/总线机制本身的验收套件**（校验、包装器栈、来源归因、band 排序、
   控制流、挂载语义）。删类型 ⇒ 删套件 ⇒ 机制失去守护；R7 明确禁止能力退化。
2. `613` 处 `Effect*` 观测点（87 文件）确实可按 S14 第 5 条改成"审查面存在某条边／组件已装配"，
   **但前提是新面真的在跑**。当前 `EmitAsync` 生产调用点为 0，改写后的断言会**恒假**；
   为了让它们变绿只能同时删断言或删用例——正是硬约束 2 禁止的"删测试换绿"。因此本轮**不改写**，等 A2 落地后一次性改写。

### 15.5 完成判据实测（可复跑）

```text
grep -rn "new Trigger<" src/ | wc -l                          → 38（17 文件）        ✗ 未达 0（阻塞：A2／A3）
grep -rn "\bBus\b" src/ | wc -l                               → 40                  ✗ 未达 0（阻塞：A2）
grep -rn "ActionTriggerBands\|ActionHandleBands" src tests    → 0                   ✓
grep -rn "TriggerEventInfo.*BandValue\|\.BandValue" src       → 0（公开面）          ✓
grep -rn "BandValue" src/                                     → 9（全部内部排序槽/校验，见 15.1）◐
dotnet test … --artifacts-path artifacts/test-u5b-b2          → 2602 / 0 / 0         ✓
```

- **`new Trigger<` = 0 与 `Bus` = 0 在本轮不可达**：两者是同一张依赖图的两端——生产发射走 `Bus`，
  `Bus` 把更新投给 `Trigger<TView>`；`OrchestrationRuntime.EmitAsync` 是唯一替代通道，生产调用点为 **0**。
  先删任一端都等于关掉效果系统。**解锁顺序固定为：A2（发射切成员）→ b4（显示名链）→ b5（类型删除）**。

### 15.6 被删测试清单（含理由）

**无。** 本轮删除测试 **0 条**、跳过 **0 条**、弱化 **0 条**；唯一测试侧改动是 `AuditChainExportTests`
**增加 2 条断言**（15.3）。全量条数 2602 → 2602，与基线逐项一致。

### 15.7 建议的后续拆除顺序（每步都可独立验收）

| 步 | 前置（硬） | 动作 | 验收 |
| --- | --- | --- | --- |
| **A2** | 无 | `LogicEngine.Emit` → 调 `engine#<信号>` 成员（双写期：先并联、后切断）；`GameUpdates` 的 36 个发射点改经新面 | 既有 1457 条游戏层测试全绿；`grep "\.EmitAsync(" src` 从 0 变为"覆盖全部信号" |
| b4 | A2＋旧格式资产不再被装载 | 删 `MatchNamedTriggers`／`LegacyDisplayNames`／`TryFindNamedTrigger`／`Match.cs` 4 工厂 | `grep "伤害处理触发器" src` = 0；`DiyExample09` 走 v4 仍绿 |
| b3 | b4（显示名不再解析实例） | `TriggerRegistryEntry.Trigger` 删除；加载器实例登记改地址（需先定 `loader#<…>` 命名） | 注册表 21 端全按地址；`entry.Trigger` 断言清零 |
| b2（清单项） | b3 | `ActionEnds` 31 处消费清零（跨程序集取实例的桥） | `grep "ActionEnds\." src tests` = 0 |
| b5 | A2＋A3＋b3/b4 | 删 `Trigger<TView>`／`ITriggerMetadata`／`TriggerKind`／`TriggerEvent`／`TriggerRegistration`／`Effect*`／`Bus`／`HookRegistry`／`Prefabs` 旧 DTO | `new Trigger<` = 0；`Bus` = 0；全量绿；契约基线重生成 |
| A5 | b5 同批 | 613 处 `Effect*` 断言改写成"审查面边/组件"断言 | 全量绿；逐条记录改写理由 |

> **顺序不可交换**：b4 依赖 A2（显示名解析的是实例）；b3 依赖 b4（否则无地址可替代）；b5 依赖全部。
> 本轮把 b1/b2 做完后，**下一轮的起点就是 A2**，不是继续删类型。

### 15.8 本轮确认／证伪的假设

1. **"band 两个枚举是零消费者，删了就行"**：**成立**（`grep` 双零），但连带有 **3 处 XML `cref`** 需要同步
   （`CommandManager`／`AttackAction`）——编译期不报错，文档期会悬空。已同批修掉。
2. **"`BandValue` 整个删掉"**：**部分证伪**。公开面可删（0 测试依赖），**内部排序槽不能删**：
   `OrderFlow` 的 `Counter(1) → Resolve(2)`／`Finalize(3)` 仍靠它，删了会反转"反制 → 宣告"的次序（§A3 未裁决）。
3. **"删 `Trigger<TView>` 只是机械改 221 处引用"**：**证伪**。测试侧 174 处实例化是机制自身的验收套件，
   不是"旧类型存在"的断言——删除等于删掉守护（R7）。
4. **"605/613 处 `Effect*` 断言现在就能改写"**：**证伪**（本轮新增证据）。新面生产调用点为 0，
   改写后的"审查面有边／组件已装配"断言会恒假；只能等 A2 让 v4 真正被信号驱动。
5. **"`ActionEnds` 31 处可以先清零"**：**未做，且不建议**。它是 `Orc.Foundation.Actions → Orc.Game`
   跨程序集取实例的唯一桥；清零必须把实例面重新公开（回到 U3 之前），反而推迟 b5。

### 15.9 下一轮开工清单（照抄即可）

```text
1) A2：Match.EngineRuntime 接线——GameUpdates 36 个发射点 → engine#<信号> 成员；Bus.Emit 双写收敛。
2) 验收：1457 条游戏层测试全绿；新增"同一时机只发一次"的守护测试。
3) 然后才回到 b4 → b3 → b2 → b5（顺序见 15.7）。
```


---

## 十六、A2 执行记录：生产发射从 `Bus` 切到 `engine#<信号>`

> **一句话结论**：发射与挂载**双向切换完成**——`LogicEngine.Emit` → `SignalBridge` → 对 `engine#<信号>` 成员的调用，
> 订阅＝该成员上的一条边；`Bus` 从"第二套机制"降级为**同一实现的旧调用形态门面**（每个方法都转发，不含独立订阅表）。
> **生产路径上 `Bus` 的消费者＝0**（剩余 18 处引用＝定义 5 ＋ XML 文档 13，见 16.7）。
> 全量 **2613 / 0 / 0**（基线 2602 ＋ 11 条新增等价与守护用例；删除／跳过／弱化 0 条）。

### 16.1 改了什么（三个落点，不是 40 处调用点）

| 落点 | 文件 | 说明 |
| --- | --- | --- |
| 发射漏斗 | `src/Orc/Core/SignalBridge.cs`（新）＋`LogicEngine.Emit` | 36 处 `GameUpdates.Emit*` ＋ 4 处直接 `Emit` 本来就全汇聚在 `LogicEngine.Emit` 一个方法上——只改漏斗，调用点一行未动（信号名、载荷键、调用顺序全部原样） |
| 挂载面 | `SignalBridge.Mount` ＋ `LogicEngine.Mount<TView>`／`UnmountOwner` | 8 处生产 `Bus.Mount` 全部改签名（`Bus` → `LogicEngine`）：`Effect.ExecuteMount`／`MountDisposal`／`MountMainTrigger`（含 `DynamicEffect` 的反射挂载）、`CardLoadoutProcessor`、`RetriggerSystem`、`CommandManager` 双触发器 |
| 读面 | `ShapeTools`（MCP `emit` 工具） | `Bus.GetSubscribers` → `BuildAuditGraph().EdgesInto(signal)`：**唯一**的生产订阅查询读点，换成审查面边（旧读面的等价定义就是它） |

**为什么必须同时把 `Bus` 变成门面**（不是"顺手重构"）：切换后 `engine.Emit` 走新路、`Bus.Mount` 仍走旧路，两条路各承担一半——
实测第一轮全量就红 35 条（21 内核机制 ＋ 14 游戏场景），全部是"用 `engine.Emit` 发射、用 `Bus.Mount` 订阅"的既有用例。
把它们改成新 API 等于删掉"优先级排序／快照语义／取消穿透"等机制验收（R7 禁止）；因此选择**同一实现、两套调用形态**：
`Bus` 的每个方法都转发到新通路，`BusMountTests`／`BusEmitTests` 的 21 条断言因此**原封不动**地继续生效，
并且从此验证的是新实现的语义（这正是 A2 需要的等价证据）。

### 16.2 等价性承载点（逐条 → 由谁保证 → 谁在测）

| 维度 | 旧总线 | A2 后 | 证据 |
| --- | --- | --- | --- |
| 更新条目 | 先写 `WriteUpdate("bus", 信号名, Info, [信号名], payload)` | **逐字相同**（同一调用、同一参数） | `BusStreamAndEngineTests`（source/keywords/等级）、`SignalFamilyEquivalenceTests`（39 信号逐个断言） |
| 顺序 | 条目 → 外部观察者（桥/回调/即时） → 订阅者 | **相同**（`EmitAsync` 内三步同序） | `SignalFamilyEquivalenceTests`（每信号断言 `observer → subscriber`） |
| 载荷 | 每个订阅者拿**原载荷的浅拷贝** | **相同**（`CapturedPayload` 快照，每订阅者 `new Dictionary<>`） | 新增 `Subscribers_GetIndependentPayloadCopies`＋族用例逐键断言 |
| 执行序 | 挂载优先级升序 → 挂载序升序 | **同构**：`DeclarationOrder(priority, 挂载序, 0)`（R1g 下"由边位次推出"的形态不变） | `BusEmitTests.Emit_Orders_By_Mount_Priority_Ascending…`／`Emit_Executes_Lower_Priority_Values_First` |
| 快照语义 | 发射时取订阅者快照 | 边表 `Snapshot()`（同一时点快照） | `Emit_Snapshot_Semantics_Unmount_During_Broadcast…` |
| 异常隔离 | 非取消类：记录进发射者流（source＝展示名）＋继续 | **相同**（`InvokeLegacyAsync` 逐字复制记录形态） | `Emit_Event_Exception_Is_Isolated_And_Broadcast_Continues…`／`Emit_Binding_Failure_Is_Recorded…` |
| 取消穿透 | 取消类原样上抛、停止后续 | **相同** | `Emit_Subscriber_Cancellation_Stops_Remaining_And_Propagates` |
| 挂载／卸载留痕 | `source="bus"`、keywords＝[动作, 展示名, 各 hook…] | **逐字相同** | `BusMountTests`（等值断言 keywords 与 source） |
| 挂载状态 | `Trigger.MountedBus`（实例级、跨引擎拒绝） | `Trigger.MountedOnSignals`（实例级、跨引擎拒绝）——**单一真源**，旧字段已删 | `Mount_Rejects_Duplicate_Mount…`／`Mount_Rejects_Cross_Bus_Duplicate_Mount` |
| 审查面 | `Bus.Subscriptions()` 投影成 `legacy-*` 实体／边 | **同一投影**，但数据源换成端点的边（`CollectSignalEndpoints`），形态字段逐项一致 | `AuditGraphTests.LegacySubscriptions_AreProjectedAsEdges`／`AuditChainTestKit` 全量 |
| 未声明名字 | 自由字符串直接广播 | **挂载不注册**（保留"图缺口"呈现）→ **首次发射经 R1e 注册** | `UnknownHook_IsAGraphGap`／`JSON_edges段…`／新增 `SignalBridgeTests` 3 条 |

### 16.3 与 §15.7 预案的差异（两条，都是实测逼出来的）

| 预案 | 实际 | 原因 |
| --- | --- | --- |
| "双写期：先并联、后切断" | **未采用** | 双写＝每个订阅者执行两次（效果静默翻倍），没有可观测的"半绿"中间态；不是安全网，是放大器 |
| "按信号族分批，每族一次全量" | **结构上不可行**，改为按**层**分批 | 旧总线的挂载粒度是"整个触发器"（一次 `Mount` 覆盖其全部 hooks），而发射是单一漏斗。按族切＝同一触发器的 hooks 一半在新路、一半在旧路，没有任何配置能表达这种状态。故本轮分批单位＝发射漏斗／挂载面／门面化／审查面，**每层后跑全量**（阶梯见 16.5） |

### 16.4 端点模型（桥的实现形态）

一次旧触发器挂载 ＝ 一个 `legacy.signal.endpoint` 组件实体：

```text
触发器 t（展示名 "cnt.default/main"，hooks: [card.played]）
  └─ 端点实体 "cnt.default"（Bound＝t.Owner；审查面按所有者身份投影）
       └─ 运行期注册成员 "cnt.default/main"（OwnedEdge＝执行 t；Callable|Mountable）
            └─ 边：engine#card.played（source＝explicit｜framework:disposal，位次＝(priority, 挂载序)）
```

- 卸载＝逐句柄 `Revoke()` ＋ 端点实体销毁（`Destroy()` 会撤销它创建的全部边并退出存活集合）——句柄级精确撤销，不扫表（R1f）。
- 审查面读数：`EdgesInto("card.played")` 直接得到 `Member＝"cnt.default/main"`、`SourceObject＝效果实例`，
  与 A2 前读 `Bus.GetSubscribers` 的信息量一致，且多了"来源／位次／来源对象"三列。
- `legacy.signal.endpoint` 预制体登记进 `KernelSignals.CreateCatalog()` 与 `GameEnginePrefabs.CreateCatalog()`
  （运行期层、无成员、`Hosts=None`——不进宿主类型表，不影响 `host#` 解析）。

### 16.5 全量阶梯（每层一次全量，均带 `--artifacts-path`）

| 批次 | 内容 | artifacts | 结果 |
| --- | --- | --- | --- |
| 起点 | U5-b b2 终态（复验） | `artifacts/test-u5b-b2` | **2602 / 0 / 0** |
| ① 原子切换 | 挂载＋发射同时切到桥；`Bus` 仍是独立实现 | `test-a2-full2` | **2567 / 0 / 35 红**（21 内核 ＋ 14 游戏）— 全部是"新发射／旧挂载"错配 |
| ② `Bus` 门面化 | `Bus` 每个方法转发到同一实现（删掉独立订阅表） | `test-a2-b3` | **2586 / 0 / 16 红**（14 内核：审查图重复投影＋图缺口；2 游戏） |
| ③ 审查面收口 | 端点边只有一处投影、挂载不注册未声明名字 | `test-a2-core2` | 内核 **471 / 0 / 0** |
| ④ 全量复验 | ②③ 合并后 | `test-a2-full3` | **2602 / 0 / 0** |
| ⑤ 新增等价测试 | ＋11 条（见 16.6） | `test-a2-final` | **2613 / 0 / 0** |
| ⑥ 终验 | 文档口径同步后复跑 | `test-a2-final2` | **2613 / 0 / 0** |

分布（终态）：Orc.Tests **474**（471＋3）、Orc.Game.Tests **1465**（1457＋8）、Orc.Packages **372**、Orc.Mcp **118**、
Orc.Server **112**、Orc.Script **39**、Orc.Manager **20**、Orc.Ai **13**。

### 16.6 测试改动（逐条理由；无删除／无跳过／无弱化）

**唯一被改写的既有用例（1 条，1→1）**

| 用例 | 改动 | 理由 |
| --- | --- | --- |
| `EngineEntityTests.ComponentEdgesAreLive_AndProductionStillEmitsThroughLegacyBus` → `…AndProductionEmitsThroughEngineSignals` | ②原断言"src 里**没有** `EmitAsync(` 调用点"反转为"只允许出现在漏斗三文件（`LogicEngine.cs`／`SignalBridge.cs`／`Model/Signals.cs`）"；③新增"src 里没有 `.Bus.`／`Bus.Mount(`／`Bus.Emit(`（`Bus.cs` 除外）" | 这条是**方向性绊线**：A2 前它守"生产不接新面"，A2 后必须守"生产只有新面、且只有一条"。若不改，它等价于要求"永远不做 A2"。断言强度不变（仍是全量源码扫描），且**多守了一条**（总线消费者归零） |

**新增（11 条，全部是等价／守护证据）**

| 文件 | 条数 | 内容 |
| --- | --- | --- |
| `tests/Orc.Game.Tests/SignalFamilyEquivalenceTests.cs` | 8 | 6 条族等价（内核 4／回合 5／卡牌 7／死亡离场 5／单位 9／反制与资源 9 ＝ **39 个信号全覆盖**，逐信号断言顺序＋载荷＋恰一次＋审查面边）＋1 条"族表与声明表逐项一致"守护＋1 条"订阅者载荷互不共享" |
| `tests/Orc.Tests/Model/SignalBridgeTests.cs` | 3 | 未注册名字＝图缺口；运行期注册（R1e）后可挂载／可发射且审查面变真实边；未注册名字首次发射自动注册 |

**没有测试需要从 `GetSubscribers` 改写成边断言**：U5-a 已把游戏层断言面统一到 `AuditTestKit.EdgesInto`（两处 `AuditTestKit`），
本轮唯一的 `GetSubscribers` 生产读点是 MCP `emit` 工具，已改为边读取（16.1 第三行）——因此"逐条列理由"实际只有上面 1 条。

### 16.7 `Bus` 在 src 内的剩余引用清单（文件:行，供下一轮删除）

```text
$ grep -rn "\bBus\b" src/ --include=*.cs | grep -v "class Bus"
```

| 类别 | 位置 | 是否消费者 | 处理 |
| --- | --- | --- | --- |
| **类型定义本体** | `Core/Bus.cs:12`（`sealed class Bus`，被 `grep -v` 排除）、`:15`／`:23`／`:93`（自身 cref／ctor） | — | b3 整文件删除 |
| **引擎持有面** | `Core/LogicEngine.cs:38`（`Bus = new Bus(this)`）、`:58`（`public Bus Bus { get; }`） | 创建／暴露，**无调用方** | b3 同批删除（`LogicEngine.Bus` 一并删） |
| **XML 文档引用（13 行）** | `Core/LogicEngine.cs:10,18,131,169,204,229,241`；`Core/Model/Signals.cs:202`；`Core/Model/ComponentAssembly.cs:18`；`Core/Model/AuditGraph.cs:96`；`Core/SignalBridge.cs:214,223`；`Game/Match/Match.cs:199` | 否（纯历史／等价性说明） | b3 时随文改词或删除 |

**结论：生产可执行引用 0 处**——没有 `engine.Bus.X` 调用、没有 `Bus.Emit`／`Mount`／`UnmountOwner`／`GetSubscribers`／
`Subscriptions()` 的调用点。总线只剩"类型 + 引擎上的一个属性 + 注释"。

### 16.8 本轮发现的两个风险（交 A3）

1. **v4 执行面从 0 变为"可用但未在 Match 里验证"**：A2 之前 `EmitAsync` 生产调用点为 0，v4 组件的边永远不执行；
   现在它们会被执行。`MigratedAssetEquivalenceTests` 等已通过**显式 `EmitAsync`** 验证过 handler 正确性，
   但**没有任何用例把含 `effects-v4/` 的包装进 `Match` 后经生产发射验证**（`samples` 里有 5 个包、20 个组件资产，
   游戏层测试只装载旧 `effects/` 目录）。→ **A3 第一件事**：装一个 v4 包跑一局，确认"边建了→信号来了→handler 真跑"。
2. **`one-shot`／`framework:disposal` 与新桥的交互只走"边"这一条**：旧路径的注销兜底是 `disposing` 触发器
   （现挂在 `card.destroyed`／`effect.removed` 的端点上）；v4 组件的注销是 `framework:disposal` 边。
   两者现在共用同一张边表（前者 `Declared(priority,…)`、后者 `FrameworkLast(component)`），
   位次上 v4 组件边（component 1..N）恒在旧端点边（component 100..300）之前——**A2 没有改变任何既有观测**，
   但这是"新面先跑、旧面后跑"的第一次真实并存，A3 需要一条"同信号上 v4 组件 + 旧效果"的联合用例钉住它。

### 16.9 完成判据实测（可复跑）

```text
grep -rn "\.Bus\." src/ --include=*.cs | grep -v "Core/Bus.cs"        → 0        ✓
grep -rn "Bus\.Mount(\|Bus\.Emit(\|Bus\.UnmountOwner(\|GetSubscribers(" src   → 0（仅 Bus.cs 自身与注释）✓
grep -rn "\.EmitAsync(" src | grep -v "Model/Signals.cs"              → 1（LogicEngine 的唯一转发点）✓
grep -rn "\bBus\b" src/ | wc -l                                       → 19（含 class 行；定义 5 ＋ 文档 13）✓
grep -rn "\bBus\b" src/ | grep -v "class Bus" | wc -l                 → 18 ✓
dotnet test … --artifacts-path artifacts/test-a2-final2                → 2613 / 0 / 0 ✓
```

### 16.10 下一轮起点建议

**A3（效果执行迁到组件）优先于 b3/b4**，理由：

1. A2 已把"新面真的在跑"从 §15.4 的硬阻塞（`EmitAsync` 生产调用点 0）变成事实——`b5` 的 A2 前置条件**已解除**；
2. 但 `b4`／`b3` 仍被"旧格式资产仍被执行路径装载"与"显示名解析实例"卡住，而这两者的根因都是 `Effect` 子类承载执行（A3）；
3. A3 的入口已经现成：`Effect.Injections` 的审查读点（`AuditGraphBuilder`）＋ `ActiveEffect : ICastAction` 的施放入口
   ＋ 16.8 第 1 条的"v4 包跑一局"验证脚本（建议做成 `Orc.Game.Tests` 里的一条端到端用例）。

若必须先动 b 系：**b4 的第一步只能是"让旧格式资产不再被装载"**（`AssetPackLoader` 对旧 `effects/` 目录的装载开关），
不是删 `MatchNamedTriggers`——删了只会让 `DiyExample09` 立刻红（§A4.6 已实测）。

> **本节已被「十七」执行**（A3）：16.8 的两条风险均已闭环——第 1 条在真实 `Match` 里跑通，且**暴露了一个必须先修的失效**（视图 `Host` 是实体而非卡）；
> 第 2 条已落成联合次序用例，并顺带修正了"审查面位次可看出新面先跑"这个此前**并不成立**的假设。

---

## 十七、A3 执行记录：效果执行从 `Effect` 子类迁到组件实体

> **一句话结论**：v4 包第一次在**真实 `Match`** 里跑通（此前只经测试显式 `EmitAsync` 验证过 handler 正确性），
> 首个端到端用例立刻抓到一处**全量静默失效**——迁移后的 csx 全部写 `view.Host is Orc.Cards.Card`，
> 而承载视图给出的是 `OrchestrationEntity`（宿主实体），于是每条按宿主过滤的 handler 都提前 `return`；
> 修正宿主投影后，`Effect.Injections` 的**图面**读点下线（改由组件挂载边表达；nodes 面降级为旧格式专用，
> v4 天然不执行），指令施放入口改由 `CardCastActions` 判定"组件面 / 旧面"，并补了两条 A2 遗留风险的守护用例。
> 全量 **2617 / 0 / 0**（基线 2613 ＋ 4 条新增；删除／跳过／弱化 0 条）。

### 17.1 第一件事（16.8 第 1 条）：v4 包在 `Match` 内跑通——以及它抓到的真实失效

**用例**：`tests/Orc.Game.Tests/V4PackInMatchTests.v4包在Match内_组件边建立_生产信号到达_handler真跑`

链路（全部生产组件，无测试专用接线）：

```text
samples/card-pack-demo
  → PackageRegistrar.Register（cards/ + cards-support/ + effects-v4/，卡 id 前缀 pack/demo/）
  → CardPackAssembler.Attach（只装 v4，组件进 Engine.Model.Catalog）
  → Match.Initialize（卡加载 → CardBase.LoadModelComponents → AddComponent → Bind → engine#unit.deployed 边）
  → PlayManager.PlayUnitAsync（真实部署链）→ GameUpdates.EmitUnitDeployed → LogicEngine.Emit
  → SignalBridge.EmitAsync → engine#unit.deployed 成员执行 → demo.rally handler → EffectRuntime.DrawAsync
```

断言三层（"边建了 → 信号来了 → handler 真跑"）：

| 层 | 断言 |
| --- | --- |
| 边建了 | `BuildAuditGraph().EdgesInto("unit.deployed")` 中恰一条 `DeclaringPrefab=demo.rally`、`Source=explicit`、`OrderSource=declared`、`GraphGap=false` |
| 无旧面 | `Assert.Empty(vanguard.Effects)`——v4 卡在生产路径上**没有** `Effect` 实例 |
| handler 真跑 | 打出后 `player.Deck.Count == before-1` 且 `UpdateRecorder` 恰 1 条 `card.drawn`（世界状态＋真实信号链，不是直改容器） |

**抓到的失效（修复前 → 修复后）**：修复前该用例红在"卡组 6 ≠ 5"——handler 执行了但**提前返回**。
根因不在信号、也不在边：`OrchestrationEntity.ExecuteMemberAsync` 把 `Host`（`OrchestrationEntity`）直接塞进
`ViewCarrier`，而迁移后的脚本写的是：

```csharp
if (view.Host is not Orc.Cards.Card self) { return; }   // 全部 v4 资产的固定开头
```

于是 `view.Host`（实体）永远不是 `Card` ⇒ **每一条**按宿主过滤的 handler 都静默 no-op。这与
`requirements.md:138`／`implementation.md:225` 的硬要求（"`view.Host is Orc.Cards.Card` 转换语义不变"）直接冲突，
是 A2 之后"新面可用但没人跑过"的必然盲区。

- 修复：`OrchestrationEntity.HostForView => Host is null ? null : Host.Bound ?? Host`（`OrchestrationEntity.cs:102`），
  视图绑定（`:284`）与包装器视图绑定（`:296`）**同源**改用该投影；`owner` 仍是实体（`owner.<私有字段>` 通道不变）。
- 为什么不是"改资产"：26 个已迁移组件的脚本都以 `view.Host is …Card` 开头，改资产＝把迁移口径改成第二套；
  且 `implementation.md` 已裁决转换语义不变。修投影是唯一符合既定契约的落点。

### 17.2 `Effect.Injections` 的审查读点 → 组件挂载边

| 落点 | 改前 | 改后 |
| --- | --- | --- |
| `AuditGraphBuilder`（**图**面） | `CollectLegacyInjections` 遍历 `card.Effects[].Injections`，投影成 `legacy#<稳定键>` 目标、`injected` 来源、`GraphGap=true` 的边（每卡每注入一条） | **删除该投影**。v4 的 `injects` 已是组件 `mounts`，由 `CollectModel` 投影成**真实边**：`Target=engine#<成员>`、`DeclaringKind=component`、`GraphGap=false`、位次可读 |
| `OrchestrationManager.CollectAll`（**nodes** 面） | 注入目标触发器作为节点实例来源之一（`Origin="injection"`） | **保留，但降级为旧格式专用**：v4 卡的 `Effects` 为空 ⇒ 天然 no-op；`nodes` 是"触发器种类"图，流程触发器（Active、不挂载）**只有被注入时才第一次出现**在节点里，删掉＝旧包审查能力退化（R7）。b3 随 `Effect` 删除 |

> 两个面必须分开看：**图**面（谁挂在谁上）已完全由组件边承载；**nodes** 面（有哪些触发器种类）是旧机制的残影，
> v4 没有"触发器种类"这层身份，故 v4 不需要它，而旧包仍需要它。

**顺带修正：审查面位次此前不是全局序**。`CollectModel` 与 `CollectSignalEndpoints` 各自从 1 开始编号 ⇒
同一条信号上"组件边 + 旧端点边"**都显示 `Position=1`**（§16.8 第 2 条据此说"位次上 v4 在前"其实**不成立**）。
现改为：模型边按"该成员上已投影的模型边数"编号，旧端点投影从 `modelEdgeCounts[hook]` 接着数。
- 等价性：既有三处位次断言（`BusMountTests` 1/2/3、`SignalTests` 1/2、`AuditGraphTests` 1..4）**全部原样通过**
  （它们场景内只有一类边，编号不变）；
- 新增证据：联合用例里 `v4.Position=1`、`legacy.Position=2`（改前两者皆 1）。

**等价用例**（新增，1 条）：`MigratedAssetEquivalenceTests.injects迁移后审查面读组件边_不再有旧injected边`
—— 同一份 `injects 无 band` 资产迁移后：边存在且**信息量不低于旧面**（目标地址/成员/来源/位次/`GraphGap=false`），
同时 `EdgesBySource(Injected)` 为空、无 `legacy#` 目标（旧读面确已下线）。

### 17.3 `ActiveEffect : ICastAction` 的施放入口 → 组件成员

**先厘清事实**（避免把"已经迁完"当成"还没迁"）：v4 指令卡的主动效果在资产迁移时已从
`injects/hooks` 变成 `mounts: engine#card.played`；`OrderFlow` 在 Resolve 的**宣告点**（priority 0）发射
`card.played`，组件边随之执行。因此**生产施放本来就是组件成员**，`card.Effects` 对 v4 卡为空。

本轮把这件事变成**结构保证 + 可测断言**，而不是"碰巧为空"：

| 落点 | 改动 |
| --- | --- |
| 新增 `src/Orc.Game/Cards/CardCastActions.cs` | 唯一解析点：卡实体上**有已装配组件** ⇒ 返回空集（施放由 `engine#card.played` 的组件边承载）；否则回退 `card.Effects.OfType<ICastAction>()`（C# 夹具／旧格式） |
| `OrderAction.ExecuteAsync` | `card.Effects.OfType<ICastAction>()` → `CardCastActions.Of(card)` |

- **行为等价**：判据用"有已装配组件"而非"有实体"——卡实体在 A1 之后对**所有**卡都存在；用"有实体"会把仍走旧面的
  C# 指令卡（`PlayChainCommandTests`／`CounterEffectAssemblyTests` 的 `TestCastEffect`）误判成 v4 并**吞掉施放**。
  本轮全量里这些用例**原样通过**，即是旧面未被误伤的等价证据。
- **改的是入口，不是实现**：`ActiveEffect.CastAsync`／`DynamicActiveEffect.CastAsync` 一字未动（旧面实现保留到 b3）；
  变的是"一次施放要跑哪些主动效果"的**解析口径**（`OrderAction` 不再直接枚举 `card.Effects`）。
- **为什么必须有这道闸**：若同一张 v4 卡上再出现一个 `ICastAction`（资产回退、或后续有人加 C# 效果），
  施放会**跑两遍**（信号一次 + `CastAsync` 一次）——这是静默翻倍，不是报错。

**新增用例**：`V4PackInMatchTests.v4指令卡_施放走组件边_效果恰一次`（diy 包，经生产装载）
- `CardCastActions.Of(card)` 为空、`card.Effects` 为空；
- 审查面 `EdgesInto("card.played")` 有 `diy.fire-support.effect` 的边（施放点＝组件边）；
- 世界状态：敌方步兵防御 5 → 2（**恰 3 点**，不是 6）、施动方 HQ +5（恰一次）——与旧面
  `DiyExample01_DamageAndArmorTests` 的断言**逐项相同**（该用例本身未改，仍走旧面，构成跨面等价）。

### 17.4 16.8 第 2 条：同信号上"v4 组件边 + 旧端点边"并存的联合用例

**用例**：`V4PackInMatchTests.同一信号上v4组件边与旧端点边并存_新面先跑旧面后跑`

- 场景：同一张 v4 卡（`demo.vanguard`）的 `demo.rally` 组件边 + 测试现挂的一条旧端点（`Trigger<CardEventView>`，
  `hooks=[unit.deployed]`，默认挂载优先级 `Normal=200`）——两者在同一 `engine#unit.deployed` 上**真实并存**。
- 双证次序（缺一不可）：
  1. **审查面**：`v4.Position(1) < legacy.Position(2)`（位次统一后才有意义，见 17.2）；
  2. **世界状态**：旧 handler 读到的 `player.Deck.Count` **已经**是 v4 抽牌后的值（`before-1`）——旧面执行时新面已跑完；
     同时 `order == ["legacy"]` 证明旧面**确实执行**（不是被吞）。
- 位次为何恒成立：组件边的声明序＝`(组件段 1..N, 挂载声明序, 0)`；旧端点边＝`(旧挂载优先级 200, 挂载序, 0)`
  ——字典序比较下组件边恒在前。该用例把"靠推理得出的次序"变成"跑出来的次序"。

### 17.5 全量阶梯（每层一次全量，均带 `--artifacts-path`）

| 批次 | 内容 | artifacts | 结果 |
| --- | --- | --- | --- |
| 起点 | A2 终态（复验） | `artifacts/test-a2-final2` | **2613 / 0 / 0** |
| ① v4 端到端（红） | 新用例先跑，暴露 `view.Host` 失效 | `test-a3-probe1` | 2 条红（deck 6≠5；位次 1=1） |
| ② 宿主投影修复 | `HostForView`（仅此一处语义改动） | `test-a3-probe2` | 端到端 **1/1 绿** |
| ③ 审查读点 + 位次统一 + 施放入口 | 3 个落点 ＋ 2 条新用例 | `test-a3-probe3/4/5` | 目标用例 **10/10 绿** |
| ④ 全量 | ③ 之后 | `test-a3-full1` | **2617 / 0 / 0** |
| ⑤ 终验 | 仅测试断言补强后复跑 | `test-a3-final` | **2617 / 0 / 0** |
| ⑥ 终验二 | 位次口径收敛（只对**引擎**成员的模型边计数）后复跑 | `test-a3-final2` | **2617 / 0 / 0** |
| ⑦ 终验三 | 恢复 nodes 面的旧格式注入采样（R7）后复跑 | `test-a3-final3` | **2617 / 0 / 0** |

分布（终态）：Orc.Game.Tests **1469**（1465＋4）、Orc.Tests **474**、Orc.Packages.Tests **372**、Orc.Mcp.Tests **118**、
Orc.Server.Tests **112**、Orc.Script.Tests **39**、Orc.Manager.Tests **20**、Orc.Ai.Tests **13**。合计 **2617**。

### 17.6 测试改动（逐条理由；无删除／无跳过／无弱化）

**新增（4 条，全部是 A3 的验收证据）**

| 文件 | 用例 | 钉住什么 |
| --- | --- | --- |
| `V4PackInMatchTests` | `v4包在Match内_组件边建立_生产信号到达_handler真跑` | 生产装载 → 边 → 信号 → handler → 世界状态（16.8 第 1 条） |
| 同文件 | `同一信号上v4组件边与旧端点边并存_新面先跑旧面后跑` | 第一次真实并存的执行序（16.8 第 2 条） |
| 同文件 | `v4指令卡_施放走组件边_效果恰一次` | 施放入口在组件面、且不会双跑 |
| `MigratedAssetEquivalenceTests` | `injects迁移后审查面读组件边_不再有旧injected边` | 审查读点迁移的等价性（信息量不降） |

**被改写的既有用例：0 条。被删／跳过／弱化的断言：0 条。**
唯一"触碰既有断言"的是 §16.8 第 2 条的位次断言——它此前**并不存在**（无人断言过跨投影位次），本轮是**新增**。

### 17.7 `Effect`／`EffectSystem`／`DynamicEffect` 剩余引用清单（供 b3）

统计口径（可复跑）：逐行剔除 `//` 注释与字符串字面量后统计 `\bEffect\b`，排除同名枚举成员
（`AssetKind.Effect`／`SelectorActions.Effect`／`DamageKinds.Effect`／`PayloadKeys.Effect`，
以及 `Selectors.cs`／`DamageKinds.cs` 里两个 `const string Effect`）。含 `x.Effect` 属性访问与 `Orc.Cards.Effect` 限定名。

**A. `Effect`（类型本体）＝ src 83 处 / 18 文件；tests 46 处 / 18 文件**

| src 文件 | 处数 | 角色（b3 要处理的形态） |
| --- | --- | --- |
| `src/Orc.Game/Cards/EffectSystem.cs` | 16 | **旧装载链**：`CardEffectRegistry` 工厂表（`Func<CardBase, Effect>`）、`CardEffectLoader` 构造/装载/清理 |
| `src/Orc.Game/Cards/KeywordManager.cs` | 8 | 词条内嵌效果（`GrantWithContentAsync`／`AttachEmbeddedEffect`／回滚） |
| `src/Orc/Cards/Effect.cs` | 8 | **定义本体**（`Effect`／`PassiveEffect`／`ActiveEffect`） |
| `src/Orc/Cards/DynamicEffect.cs` | 8 | 动态效果两子类的基类引用 |
| `src/Orc/Cards/Card.cs` | 8 | 容器面：`Effects`／`AddEffect`／`RemoveEffect*`／`EffectsSnapshot` |
| `src/Orc.Game/Effects/EffectRuntime.DynamicEffects.cs` | 7 | 运行动态挂载/卸载/回滚（`DetachEffectAsync` 等） |
| `src/Orc/Cards/CardLifecycle.cs` | 6 | 装载/销毁原语（`MountEffect`／`DisposeEffect`／`CardEventView.Effect`） |
| `src/Orc.Game/Cards/KeywordComponent.cs` | 5 | 词条组件的内嵌效果集 |
| `src/Orc.Game/Effects/EffectDynamicResults.cs` | 4 | 动态挂载结果（凭据持有 `Effect`） |
| `src/Orc/Core/LogicEngine.cs` | 3 | 装载完成动作签名 `Action<Card, Effect>` |
| `src/Orc/Core/Trigger.cs` | 3 | owner-效果：宿主注入／registrant 归因／`Orc.Cards.Effect` 模式 |
| `src/Orc.Game/Cards/DeathrattleSystem.cs` | 1 | 亡语内容承载 `Effect?` |
| `src/Orc.Game/Cards/DeclarativeKeywordComponent.cs` | 1 | `EmbedEffect(result.Effect!)` |
| `src/Orc.Game/Effects/EffectRuntime.cs` | 1 | `GrantWithContentAsync(Effect? content)` |
| `src/Orc/Core/Orchestration.cs` | 1 | `DeriveHost` 模式匹配 |
| `src/Orc/Core/SignalBridge.cs` | 1 | `DescribeOwner` 模式匹配（A2 端点） |
| `src/Orc/Output/JsonValueWriter.cs` | 1 | 快照导出模式匹配 |
| `src/Orc/Core/Model/AuditGraphBuilder.cs` | 1 | `DescribeOwner` 模式匹配 |

| tests 文件 | 处数 |
| --- | --- |
| `EffectRuntimeDynamicAttachTests.cs` | 10 |
| `CounterEffectAssemblyTests.cs` | 5 |
| `CardContainerTests.cs`／`DynamicEffectTests.cs`／`EffectDisposalGameTests.cs` | 各 4 |
| `EffectLoadingPipelineTests.cs`／`PrefabRoundTripTests.cs`／`EffectSystemTests.cs` | 各 3 |
| `EffectLifecycleTests.cs`／`CardsTestFixtures.cs`／`RandomBattleKeywordChainTests.cs`／`C2ChainTestKit.cs`／`CardServiceTestInfrastructure.cs` | 各 1 |
| `KeywordEffectBatch1..4Tests.cs`／`KeywordEffectCsxTests.cs` | 各 1 |

**B. `EffectSystem`（类型本身不存在）＝ src 1 处（注释）；tests 2 处（测试类名＋注释）**

| 位置 | 形态 | 处理 |
| --- | --- | --- |
| `src/Orc.Game/Cards/EffectSystem.cs` | **文件**（45 KB；内容是 `CardEffectRegistry`＋`CardEffectLoader`） | b3 改名（文件内无 `class EffectSystem`） |
| `src/Orc.Game/Cards/CardBase.cs:81` | 注释 | 随改名同步 |
| `tests/Orc.Game.Tests/EffectSystemTests.cs:21` | 测试类名 `EffectSystemTests` | b3 改名（用例内容测的是装载/销毁机制，不删） |
| `tests/Orc.Game.Tests/MigratedAssetEquivalenceTests.cs:19` | 注释 | 随改名同步 |

**C. `DynamicEffect`（裸类型名）＝ src 1 处（注释，`CardComponentDefinition.cs:11`）；tests 0 处**——实际类型是下面四个：

| 类型 | src | tests |
| --- | --- | --- |
| `DynamicPassiveEffect` | **6** 处 / 2 文件（`DynamicEffect.cs` 4、`EffectSystem.cs` 2） | **18** 处 / 8 文件 |
| `DynamicActiveEffect` | **5** 处 / 2 文件（`DynamicEffect.cs` 4、`EffectSystem.cs` 1） | **2** 处 / 2 文件 |
| `DynamicEffectFactory` | **5** 处 / 5 文件（`KeywordManager`／`DeclarativeKeywordComponent`／`EffectSystem`／`EffectRuntime.DynamicEffects`／`DynamicEffect.cs` 各 1） | **14** 处 / 4 文件 |
| `DynamicEffectInstantiation` | **21** 处 / 2 文件（`DynamicEffect.cs` 20、`EffectSystem.cs` 1） | 0 |
| `ISerializableEffect`（标记面，随动态效果退役） | **4** 处 / 3 文件（`DynamicEffect.cs` 2、`Orchestration.cs` 1、`Effect.cs` 1） | 0 |

### 17.8 "生产路径无 `Effect` 消费者"的实测边界（重要，别把它读成"可以删了"）

**在 v4 包的生产路径上成立**（有断言）：
- 卡效果装载：v4 包不注册 `DeclarePrefab` ⇒ `CardEffectLoader` 不产出任何 `Effect`；`card.Effects` 为空（17.1／17.3 均有断言）；
- 执行：`card.destroyed` 等信号的订阅是**组件边**，不经 `Effect.MountMainTrigger`／`Injections`；
- 审查：图面只走组件边（17.2）；`Effect.Injections` 在 `src` 内只剩 **1 个读点**（`Orchestration.cs:191`，nodes 面的旧格式专用采样；
  v4 卡 `Effects` 为空 ⇒ 执行不到），`AuditGraphBuilder` 内为 **0**；
- 施放：`CardCastActions` 对 v4 卡返回空集（17.3）。

**仍不是 0 的三类（b3/b5 的活，不是 A3 漏项）**：
1. **装载机制本身**：`CardEffectRegistry`／`CardEffectLoader`／`DynamicEffectFactory`（旧格式资产与 C# 夹具仍要装载）；
2. **词条内嵌效果**：`KeywordManager`／`KeywordComponent` 走 `DynamicPassiveEffect`（`samples/card-pack-bulwark` 的
   `keywords.json` 仍绑旧 `keyword.*.prefab.json`——词条资产未迁 v4）；
3. **销毁机制**：`Effect.ExecuteDispose` 与 `Trigger` 的 owner-效果宿主注入（b3 的"按地址撤销"替代前不能动）。

`DynamicEffect` 的三类都在 2／1 里：**没有一条 v4 生产路径会走到它们**，但它们仍是旧格式包的执行面。

### 17.9 本轮确认／证伪的假设

1. **"v4 只是没在 `Match` 里验证过，接线应该是对的"**：**证伪**。接线对了，**投影错了**——`view.Host` 是实体，
   所有按宿主过滤的 handler 静默 no-op。这是本轮最有价值的发现：全量 2613 绿**不能**证明 v4 在新面对局里有效。
2. **"审查面位次能看出新面先跑"（§16.8 第 2 条的措辞）**：**证伪**。两条投影各自从 1 编号，同信号上两边的
   `Position` 都是 1；已修为"模型边先编号、端点边接着数"，既有位次断言全部原样通过。
3. **"删掉 `Effect.Injections` 投影会让审查能力退化（R7）"**：**按面分开——图面证伪，nodes 面成立**。
   图面：v4 的注入是真实边，信息量严格更多（目标地址、来源、位次、`GraphGap=false`），旧面只是 `GraphGap=true` 的占位投影；
   nodes 面：流程触发器不挂载，只能经"被注入"首次进入节点表 ⇒ 保留旧采样（v4 无 `Effects`，天然不执行）。
4. **"施放必须靠 `ICastAction` 迁移"**：**部分证伪**。v4 指令卡的施放早已是 `card.played` 上的组件边；
   真正需要补的是**闸门**（禁止同一张卡双跑），不是新的施放实现。
5. **"`Effect` 已无生产消费者，可以进 b3"**：**证伪**（见 17.8 三类）。b3 的第一步仍应是**文件名／测试类名**与
   `TriggerRegistryEntry.Trigger`，而不是删类型。

### 17.10 下一轮起点（b3/b4）

1. **b4（显示名链）** 仍未解锁：`CardEffectLoader.cs:496` 的 `injects.target` 按名解析只对**旧格式**资产有意义；
   先做"旧格式资产不再被装载"的开关（`AssetPackLoader`/`CardPackAssembler` 口径），再删 4 个工厂。
2. **b3**：`EffectSystem.cs` 改名（`CardEffectLoader.cs`）＋ `EffectSystemTests` 改名；`TriggerRegistryEntry.Trigger` 删除
   （需先定 `loader#<…>` 地址命名）。
3. **`Effect` 类型删除（b5）** 的前置新增一条：**词条资产迁 v4**（17.8 第 2 类）。在这之前删 `DynamicPassiveEffect`
   会让 Bulwark 类包的效果失效。
4. A3 已把"v4 在真实对局里跑通"变成可复跑的用例；后续任何动 `ViewCarrier`／`OrchestrationEntity` 执行面的改动，
   先跑 `V4PackInMatchTests` 三条（它们比全量更快也更灵敏）。

---

## 十八、本轮执行记录：b3 前置（词条资产迁 v4）＋ b5 的 `Bus` 删除；b3／b4 未完成（硬阻塞）

> **一句话结论**：本轮把两件**真正解锁**的事做完了——①**词条内嵌效果全部改由 v4 组件承载**（`samples/card-pack-bulwark`
> 的 `keyword.*` 绑定现在解析到 `effects-v4/*.component.json`，旧 `effects/*.prefab.json` 不进装载路径）；
> ②**`Bus` 类型删除**（它自 A2 起只是门面，`src` 内已无生产消费者）。
> **b3（`Effect` 家族）与 b4（`Trigger` 家族）仍然删不掉**，且阻塞点已经换了一批：不再是"新面没人跑"（A2/A3 已解决），
> 而是**内建词条行为本身就是 `Effect` 子类**、**卡与指令的执行桥仍是 `Trigger<TView>`**（§18.5 逐条带数字）。
> 全量 **2618 / 0 / 0**（基线 2617 ＋ 1 条新验收用例；删除／跳过／弱化 0 条）。

### 18.1 本轮做了什么（三项，逐项可独立验收）

| # | 项 | 类别 | 结果 |
| --- | --- | --- | --- |
| 1 | 词条内嵌效果迁 v4（b3 的**新增前置**） | 生产代码 ＋ 1 条新用例 | 词条授予→在宿主实体上装配 v4 组件（边即执行面）；旧格式数据壳效果降为**回退路径**（仅未迁 v4 的绑定与 C# 夹具使用） |
| 2 | `Bus` 类型删除（b5 的一项） | 生产代码 ＋ 测试机械改写 | `src/Orc/Core/Bus.cs` 删除；`LogicEngine.Bus` 属性删除；`src` 内 `\bBus\b`＝**0** |
| 3 | 文件名／测试类名收口（b3 的"改名项"） | 纯改名 | `EffectSystem.cs`→`CardEffectLoader.cs`、`EffectSystemTests`→`CardEffectLoaderTests`；`BusMountTests`／`BusEmitTests`／`BusStreamAndEngineTests`／`BusTestHelpers`→`Signal*` |

**为什么先做了 b5 的一项（顺序偏离已声明规则）**：`Bus` 在 A2 之后**生产消费者为 0**（§16.7：18 处引用＝定义 5 ＋ 文档 13），
是唯一"不依赖 b3/b4 就能删"的项；它挡在 b4 前面（`Bus.Mount<TView>` 是 `Trigger<TView>` 的一处实例化入口）。
先删它不改变 b3/b4 的阻塞关系，但把 b5 的可验收面缩小了一项。**若要求严格按 b3→b4→b5 顺序，本项应在 b4 之后重做一遍等价验收**（本轮的证据可直接复用）。

### 18.2 词条内嵌效果迁 v4（b3 的新增前置）

**问题（§17.8 第 2 类）**：`samples/card-pack-bulwark` 的 `keywords.json` 绑定 `keyword.mobility` 等 id，
而该 id 只存在于 `effects/keyword.*.prefab.json`（旧格式）；`KeywordManager` 经 `DynamicEffectFactory` 造出
`DynamicPassiveEffect` 挂到词条组件上 ⇒ **删 `DynamicPassiveEffect` 就会让铜墙铁壁类词条静默失效**。

**做法（三个落点，选路在装载期一次判定）**

| 落点 | 改动 |
| --- | --- |
| `KeywordManager.AttachKeywordEffects`（原 `AttachKeywordEffectPrefabs`） | 每个绑定先试 **v4 组件形态**：`context.Engine.Model.Catalog.Contains(prefabId)` ＋ 宿主卡有编排实体 ⇒ `runtime.AddComponent(entity, prefabId, name)`，句柄存进词条组件；否则回退旧数据壳效果（`DynamicEffectFactory`）。两条形态同一条生灭链 |
| `KeywordComponent` | 新增 `AttachedComponents`／`AttachComponent`／`DisposeAttachedComponents`（v4 挂载态的持有与逐句柄撤销） |
| `KeywordEffectAssembly.VerifyBindings` | 绑定核验从"只看旧效果库键"改成"**v4 目录或旧库键二者之一可解析**"——与装载期选路同一判据（否则只提供 `effects-v4/` 的包会在装配期 fail-fast） |

**生灭语义（与旧形态逐项对齐）**：授予＝装配（`AddComponent` 即 `Assemble`，建边）；移除／死亡注销／授予失败回滚
＝`DisposeAttachedComponents`（逐句柄撤销、幂等、零残留）；卡销毁＝`OrchestrationEntity.Destroy` → `DisposeComponents`
已覆盖（`ComponentAttachment.Dispose` 幂等，重复撤销无副作用）。

**验收用例（新增 1 条）**：`tests/Orc.Game.Tests/BulwarkV4KeywordTests.词条效果走v4组件_旧预制体不装载_固守行为等价`
- ④ 断言：`Prefabs.TryGetPrefab("keyword.entrench")` ＝ false（旧面确实没有）、`Model.Catalog.Contains` ＝ true；
- 授予后：宿主实体上恰一份 `keyword.entrench` 组件、`IsAssembled`、有边，且 `card.Effects` **为空**（没有 `Effect` 实例）；
- 行为：固守＝不能指挥移动、可被效果位移；移除词条后组件挂载态消失、指挥移动恢复。

> 该用例**不改任何既有用例**：`BulwarkKeywordTests`（手装旧预制体）与 `SpaceWarKeywordTests` 仍原样通过——
> 即"回退路径未被写坏"的等价证据。

### 18.3 `Bus` 删除

| 落点 | 改动 |
| --- | --- |
| `src/Orc/Core/Bus.cs` | **整文件删除**（含 `HookSubscriptions`——全仓 0 引用） |
| `LogicEngine` | `Bus` 属性与构造期 `new Bus(this)` 删除；文档里"Bus 是门面"的措辞改为"唯一挂载/发射面" |
| 测试（14 个文件） | `engine.Bus.Mount/Emit/UnmountOwner` → `engine.*`（**纯转发等价改写**，`Bus` 的每个方法原本就只转发；`Bus.Mount` 的 Active/重复挂载拒绝集与 `LogicEngine.Mount` 逐字相同） |
| `EngineEntityTests`（绊线） | ③ 从"除 `Bus.cs` 外无 `.Bus.`"升级为"**`Bus.cs` 不存在** ＋ 全 src 无 `.Bus.`"（扫的是字符串字面量 `".Bus."`，不是裸 `.`——原实现被批替换写成 `"."`，本轮已修正并加文件缺失断言） |
| 3 个测试套件／1 个夹具 | 改名 `Bus*` → `Signal*`（`SignalMountTests`／`SignalEmitTests`／`SignalStreamAndEngineTests`／`SignalTestHelpers`）：**内容测的是信号端点机制**，名字留着会误导 |

### 18.4 测试改动（逐条：新增／改写／删除断言）

**新增（1 条）**：`BulwarkV4KeywordTests.词条效果走v4组件_旧预制体不装载_固守行为等价`（§18.2）。

**改写（2 条用例，因"被比较／被断言的对象已不存在"）**

| 用例（原名 → 新名） | 删掉的断言 | 保留/新增的断言 | 理由 |
| --- | --- | --- | --- |
| `Engine_Bus_Is_Reachable_Stable_And_Multi_Instance` → `Engine_Stream_Is_Stable_And_Multi_Instance` | `Assert.NotNull(engine.Bus)`、`Assert.Same(engine.Bus, engine.Bus)`、`Assert.NotSame(engine.Bus, other.Bus)` | `Assert.Same(engine.RootStream, engine.RootStream)`、`Assert.NotSame(engine.RootStream, other.RootStream)` | 三条删掉的是"**旧机制（总线门面）存在且稳定**"的直接断言，删除该类型后**无等价物**（不存在可断言的替代对象）；用例守的"引擎壳多实例独立"仍由总流断言承担 |
| `Engine_Emit_Is_Equivalent_To_Bus_Emit` → `Engine_Emit_Reaches_Mounted_Triggers_And_Writes_Root_Stream` | "等价于 Bus.Emit"的比较语义（注释与用例名） | 原有用例体全部保留（两个触发器、两条发射、trace 顺序、总流条目、子流父子） | 比较的另一端已删除；**发射语义本身**由用例体继续覆盖，未弱化 |

**机械改写（14 个测试文件、约 120 处）**：`.Bus.` → `.`。等价性依据＝`Bus` 方法体逐一转发（§18.3 表）。

**改名（不改语义）**：`EffectSystemTests` → `CardEffectLoaderTests`（类名＋文件名）；`EffectSystem.cs` → `CardEffectLoader.cs`
（文件里从来没有 `class EffectSystem`，属 §17.7 B 项的"改名项"）。

**被删测试：0 条；被跳过的测试：0 条；被弱化为"恒真"的断言：0 条。**
（`EngineEntityTests` ③ 的绊线曾被批替换写成 `line.Contains(".")`——那是恒真断言，本轮**已改回** `".Bus."` 并加文件缺失断言，属于修复而非弱化。）

### 18.5 为什么 b3／b4 仍删不掉（阻塞点已换代，逐条带数字）

**A. b3（`Effect` 家族）——三条硬阻塞，全部在 src 内**

| 阻塞 | 实测 | 删了会怎样 |
| --- | --- | --- |
| **内建词条行为就是 `Effect` 子类**（§17.8 第 2 类没说到这一层） | `KeywordEffects.cs`：基类 `KeywordSignalEffect`／`KeywordDamageRewriteEffect` ＋ 5 个具体类（`SuppressionLifecycleEffect`／`IntelligenceTriggerEffect`／`AmbushRewriteEffect`／`ArmorReductionEffect`／`ImmuneZeroingEffect`）；`DeathrattleSystem.cs`：`DeathrattleEffect : PassiveEffect` ⇒ **8 个类声明**，全部由 `KeywordComponent.EmbedEffect` 在生产路径实例化 | 被压制/被抑制/情报/伏击/重甲/免疫/亡计 **7 个内建词条**静默失效（不是报错，是 no-op） |
| **卡效果容器与装载链** | `Card.Effects`／`AddEffect`／`RemoveEffect*`／`MountPassiveEffects`（`Card.cs` 8 处）、`CardLifecycle` 6 处、`CardEffectLoader`（装载）、`EffectRuntime.DynamicEffects` 7 处、`EffectDynamicResults` 4 处、`LogicEngine` `Action<Card, Effect>` 3 处 | 旧格式卡（C# 夹具、`Prefabs` 声明的包）装不出效果；`DiyExample*`／`C2Chain*` 等 12+ 用例直接红 |
| **主动效果施放面** | `ActiveEffect<TView> : ICastAction`（`Effect.cs` 697）＋ `OrderAction` 的 `CardCastActions` 回退分支 | C# 指令卡（`PlayChainCommandTests`／`CounterEffectAssemblyTests` 的 `TestCastEffect`）失去施放实现 |

**测试侧规模（可复跑）**：`tests` 内**44 个类**继承 `PassiveEffect`／`ActiveEffect<>`（`tests/Orc.Tests/CardsTestFixtures.cs` 6 个、
`tests/Orc.Game.Tests/CardEffectLoaderTests.cs` 7 个、`C2ChainTestKit`／`CounterEffectAssemblyTests`／`DiyExample06` 等）；
`PrefabJson` 在 **18 个测试文件、74 处**用于直接装载旧格式资产。
按 S14 第 5 条改写这 44 个替身＝**把"机制验收套件"整批改写成组件面断言**，且它们测的语义（装载钩子失败回滚、托管清理、注入段次序）
在组件面**没有一一对应物**（组件面用"边＋句柄"表达）——本轮**不改写**，理由同上轮 §15.4 第 2 条：改写后若恒假，只能删断言。

**B. b4（`Trigger` 家族）——执行桥仍是旧触发器**

| 事实 | 数字 |
| --- | --- |
| `new Trigger<` 在 src | **38 处／17 文件**：`CommandManager` 7、`JoinBattlefieldAction` 4、`DamageAction` 4、`DrawAction` 3、`OrderFlow` 2、`CardLifecycle` 2、`CardMemberTable` 2、`MoveAction`／`DeathAction`／`AddToHandAction` 各 2、`Hq`／`UnitCard`／`OrderCard`／`CounterCard`／`RetriggerSystem`／`SandboxPrimitives`／`Effect.cs` 各 1 |
| `Trigger<` 总引用（src，粗口径） | **186 处** |
| 卡侧唯一物化点 | `CardMemberTable.CardLegacyTriggerFactory`：把类型预制体声明的成员**物化**成 `Trigger<TView>` 并登记旧显示名（`LegacyDisplayNames` 10 条）——b4 要删的正是这一层 |
| 测试侧 | `new Trigger<` **181 处／37 文件**（机制验收套件：挂载/发射/来源归因/band 排序/控制流） |

**结论**：b4 不是"删类型"，而是**把卡／指令／流程的执行面整体从实例触发器换成 v4 成员**（`CommandManager` 的 7 个命名触发器、
6 个动作实现的内部触发器、`OrderFlow` 的 4 个流程触发器）。这与 b3 的内建词条迁移是同一张图的两端，必须同批设计。

**C. b5 其余项（依赖 b3/b4）**

| 项 | 现状 | 依赖 |
| --- | --- | --- |
| `PrefabManager` | src 25 处（`KeywordPrefabLibrary` 4、`DynamicEffect` 3、`CardDistributionValidator` 3、`LogicEngine.Prefabs`、编辑器/迁移/MCP 资产工具） | 旧格式资产装载（b3） |
| `PrefabJson` | src 23 处、tests 74 处 | 同上 |
| `MatchNamedTriggers`／`LegacyDisplayNames`／`TryFindNamedTrigger` | 5／3／5 处；唯一生产读点是 `CardEffectLoader.cs:496` 的 `injects.target` 按名解析 | b3＋b4 |
| `AuditGraphBuilder.CollectLegacy*` | **已为 0**（A3 §17.2 删掉了 `CollectLegacyInjections` 投影，图面改走组件边） | ✅ 已完成 |
| `HookRegistry` | 5 处，全部是"写入词汇表"（`Register`）＋`HookId.FromName`，无读面 | 可随 b4 一并评估（`Trigger.Metadata` 的 hook 名来源） |

### 18.6 完成判据实测（可复跑）

```text
grep -rn "new Trigger<" src/ | wc -l            → 38（17 文件）      ✗ 未达 0（阻塞：内建词条/执行桥，§18.5）
grep -rn "\bBus\b" src/ | wc -l                 → 0                  ✓（含文档措辞一并清理）
grep -rn "class Effect" src/ | wc -l            → 29                 ✗ 未达 0（阻塞：§18.5 A）
grep -rn "AuditGraphBuilder.CollectLegacy" src/ → 0                  ✓（A3 已完成）
dotnet test … --artifacts-path artifacts/test-b3-rename → 2618 / 0 / 0  ✓
```

**逐层全量（每层一次，均带 `--artifacts-path`）**

| 层 | 内容 | artifacts | 结果 |
| --- | --- | --- | --- |
| 起点 | A3 终态复验 | `test-b3-layer0` | **2617 / 0 / 0** |
| ① b3 前置 | 词条 v4 组件承载 ＋ 1 条新用例 | `test-b3-pre-full` | **2618 / 0 / 0**（Game 1469→1470） |
| ② b5-Bus | 删 `Bus` ＋ 121 处测试改写 ＋ 2 条用例改写 | `test-b5-bus` | **2618 / 0 / 0** |
| ③ 改名 | `EffectSystem.cs`→`CardEffectLoader.cs`、`Bus*`→`Signal*` 套件 | `test-b3-rename` | **2618 / 0 / 0** |

分布（终态）：Orc.Game.Tests **1470**、Orc.Tests **474**、Orc.Packages.Tests **372**、Orc.Mcp.Tests **118**、
Orc.Server.Tests **112**、Orc.Script.Tests **39**、Orc.Manager.Tests **20**、Orc.Ai.Tests **13**。合计 **2618**。

### 18.7 未完成清单（按下一步顺序，含为什么现在不能做）

| # | 项 | 前置（硬） | 现在做会怎样 |
| --- | --- | --- | --- |
| 1 | **内建词条行为迁 v4**：`KeywordEffects.cs` 的 5 个 `Effect` 子类 ＋ `DeathrattleEffect` | 需要先定"词条行为用组件成员还是 C# handler 装配"的口径（判据：`KeywordLoadContext` 里的 `Judicators`／`DamageAction`／`DeathAction` 在 v4 面怎么取） | 删 `Effect` ⇒ 7 个内建词条静默失效（§18.5 A 第 1 条） |
| 2 | **卡／指令执行桥迁 v4 成员**：`CardMemberTable.CardLegacyTriggerFactory`、`CommandManager` 7、6 个动作实现、`OrderFlow` 4 | 需要 `I*Action` 契约再收一轮（当前仍是实例句柄，U3 已加宽到成员句柄但卡侧未接） | 删 `Trigger<TView>` ⇒ 卡加载/动作执行/流程触发器全部断 |
| 3 | **b3 收尾**：`Effect`／`PassiveEffect`／`ActiveEffect`／`DynamicEffect*`／`EffectInjection`／`ISerializableEffect` | 1＋2；且 44 个测试替身按 S14 第 5 条改写成"边/组件"断言 | 现在是"删测试换绿"（R7 禁止） |
| 4 | **b4 收尾**：`Trigger<TView>`／`ITriggerMetadata`／`TriggerKind`／`TriggerEvent`／`TriggerMetadata`／`TriggerRegistration` | 2 | 同上 |
| 5 | **b5 收尾**：`PrefabManager`／`PrefabJson`／显示名链／`HookRegistry` | 3＋4 | 旧格式资产（编辑器/MCP/迁移工具）读不出历史包 |
| 6 | 契约基线重生成（`Bus` 已删、`EffectSystem` 已改名） | 本层已满足 | **本轮未做**：仓库内未见"契约基线"生成脚本的运行入口，需人工确认（见 §18.8 第 3 条） |

### 18.8 重构完成后仍需人工确认的事项

1. **旧格式资产的退役裁决**（产品决定，不是代码决定）：`effects/*.prefab.json` 是否仍需被**编辑器/迁移工具**读取？
   `PrefabJson`／`PrefabManager` 的删除边界取决于此（§15.4 表末行）。**当前结论：执行面可退，作者面未裁决。**
2. **词条行为的承载口径**：内建词条（伏击/重甲/免疫/情报/被压制/亡计）迁 v4 时，是写成 csx handler 还是 C# handler 装配？
   影响 `view.Host is Card` 的转换语义（`implementation.md:225`）与判定器通道（`Judicators.Invoke`）的可达性。**需架构裁决后再动手。**
3. **契约基线重生成**：本轮删了 `Bus`（公开类型）、改了 `EffectSystem.cs` 文件名与 3 个测试类名。
   若存在外部契约快照（MCP 文档／SDK 基线／前端类型），需人工重生成并复核——**仓库内没有可自动执行的入口，故未做**。
4. **`BulwarkV4KeywordTests` 的测试侧词条登记**：生产路径由 `Orc.UI.CardPackKeywordLoader`（包生命周期）注册包词条；
   该用例用手工登记复刻（因为只走对局装配）。**若前端包加载链改变登记形状，该用例需同步**（它不是生产路径的替代）。
5. **`Bus` 删除的对外影响**：`LogicEngine.Bus` 是公开属性，宿主/插件若使用会在编译期报错（本仓内已清零）。
   外部宿主（`samples/Orc.*`／第三方插件）需人工确认是否需要迁移说明。
6. **`HookSubscriptions` 随 `Bus.cs` 一并删除**：全仓 0 引用，但它是公开类型；若有外部消费需恢复为独立文件。

---

## 十九、b3／b4／b5 收尾（本轮）：零删除的实测原因为何，以及逐项解锁表

> **一句话结论**：本轮按"删 `Effect`／`Trigger<TView>` 及其余旧装载链"的要求先做了**删除可行性实测**，
> 结论是**不能删**——但不是因为"44 个测试替身"（那 44 个是**消费者**，不是阻塞源），而是因为这两个类型
> 仍是**生产执行面本身**：src 内 `Effect` 有 **12 个活体子类**、`Trigger<` 有 **186 处引用／37 文件**、
> **38 处实例化**，每一处都挂在装卡／伤害／抽牌／流程的真实路径上。
> 强行删除不是"测试变红"，而是**编译不过**或**7 个内建词条静默 no-op**。
> 本轮因此**未改一行源码、未删一条测试**，把工作转成两张可执行的表：**逐项裁决（§19.3）**与**解锁路径（§19.7）**。
> 全量复验 **2618 / 0 / 0**（与起点一致）。

### 19.1 起点复验（本轮唯一一次全量）

```text
dotnet test D:/OrC-KSD/OrcEngine.sln --artifacts-path D:/OrC-KSD/artifacts/test-b345-layer0
→ Orc.Game 1470 / Orc.Tests 474 / Orc.Packages 372 / Orc.Mcp 118 / Orc.Server 112 / Orc.Script 39
  Orc.Manager 20 / Orc.Ai 13  合计 2618，失败 0，跳过 0
```

与 §18.6 终态逐项相同。**过程中未杀常驻 `Orc.Manager`**，未出现 DLL 锁导致的假红。
本轮无源码改动 ⇒ 无需第二次全量（终点＝起点；`--artifacts-path` 已隔离 `artifacts/test-b345-layer0`）。

### 19.2 "卡点＝44 个测试替身"这个判断为什么不成立

44 这个数字是**准确的**（`tests` 内继承 `Effect`／`PassiveEffect`／`ActiveEffect<>` 的类声明恰 **44** 个／21 文件），
但**归因错了**。测试替身是**旧类型的实现者**，它们只能在新类型消失**之后**才失效；在 src 还有 12 个活体子类时，
删掉测试替身既不能让 `Effect` 消失，也不会让任何生产路径变好——只会先删掉 21 个文件里的机制验收套件。

**实测三张底数（可复跑，`--include=*.cs`）**

| 度量 | 数量 | 命令 |
| --- | --- | --- |
| `Effect` 类型本体 | **1** 处声明（`src/Orc/Cards/Effect.cs:52`） | `grep -rn "class Effect\b" src/` |
| `Effect` 家族活体子类（src） | **12** 个（§19.3 表 A） | 见 §19.3 |
| `\bEffect\b` 引用（src / tests） | **142／39 文件**、**132** | `grep -rnw "Effect"` |
| `Trigger<TView>` 类型本体 | 1 处（`src/Orc/Core/Trigger.cs:54`，**1539 行**） | `grep -rn "class Trigger<" src/` |
| `Trigger<` 引用（src / tests） | **186／37 文件**、**228／42 文件** | `grep -rn "Trigger<"` |
| `new Trigger<`（src / tests） | **38／17 文件**、**181** | `grep -rn "new Trigger<"` |
| `tests` 内 `Effect` 替身类 | **44** 个／21 文件 | `grep -rnE ":\s*(PassiveEffect\|ActiveEffect<\|Effect\b)" tests/` |

> 拿 44 与 2618 比会得出"只差一步"的错觉；拿 142＋186 与 44 比，才知道**被删的测试少于被删的生产代码**，
> 这才是本轮真正要处理的对象。

### 19.3 逐项裁决（用户列出的 25 个类型，逐个给活体读点）

**表 A：b3（`Effect` 家族）**

| 类型 | 声明位置 | src 引用 | 活体读点（生产路径） | 裁决 |
| --- | --- | --- | --- | --- |
| `Effect` | `Effect.cs:52` | `\bEffect\b` 142 | `Card._effects`（`Card.cs:100` `IReadOnlyList<Effect> Effects`、`:196` `MountPassiveEffects`）、`CardLifecycle`（`MountEffect`／`DisposeEffect`）、`CardEffectLoader` 装载、`Orchestration.cs:193` nodes 采样、`JsonValueWriter` 快照 | **阻塞**：容器＋装载链本体 |
| `PassiveEffect` | `Effect.cs:681` | 10 | `KeywordComponent.EmbedEffect` 是**唯一注入口**；`DeathrattleEffect`、`KeywordSignalEffect`、`KeywordDamageRewriteEffect` 均派生于此 | **阻塞**：7 个内建词条的基类 |
| `ActiveEffect<TView>` | `Effect.cs:697` | 4 | `: ICastAction`；`CardCastActions.Of` 的回退分支（`OrderAction` 施放） | **阻塞**：C# 指令卡（`TestCastEffect`）的施放实现 |
| `DynamicPassiveEffect` | `DynamicEffect.cs:510` | 7 | `DynamicEffectFactory`（`DynamicEffect.cs:97-98`）唯一产出点 | **阻塞**：同下 |
| `DynamicActiveEffect` | `DynamicEffect.cs:571` | 6 | 同上 | **阻塞**：同下 |
| `DynamicEffectFactory` | `DynamicEffect.cs` | 6／5 文件 | 4 处生产实例化：`KeywordManager.cs:433`、`DeclarativeKeywordComponent.cs:12`、`CardEffectLoader.cs:267`、`EffectRuntime.DynamicEffects.cs:370` | **阻塞**：旧格式绑定与词条回退路径 |
| `EffectInjection` | `Effect.cs:757` | 6／1 文件 | 仅 `Effect._injections` 内部 ＋ `Orchestration.cs:193` | **随 `Effect` 一并删**，无独立价值 |
| `ISerializableEffect` | `Effect.cs:749` | 5／4 文件 | `Orchestration.cs:337`（`Serializable` 标记）＋两个动态效果实现 | **随动态效果删**；需同时决定 `Serializable` 字段的替代（v4 组件恒可序列化） |

**表 B：b4（`Trigger` 家族）**

| 类型 | 声明位置 | src 引用 | 活体读点 | 裁决 |
| --- | --- | --- | --- | --- |
| `Trigger<TView>` | `Trigger.cs:54` | 186／37 文件 | ①`CardMemberTable.CardLegacyTriggerFactory`（`:162`／`:165`）把类型预制体成员**物化**成实例；②6 个动作实现的内部管道（`DamageAction` 4、`DrawAction` 3、`JoinBattlefieldAction` 4、`MoveAction` 2、`DeathAction` 2、`AddToHandAction` 2）；③`OrderFlow` 2、`CommandManager` 7、`CardLifecycle` 2、`RetriggerSystem`、`SandboxPrimitives`、`Effect.cs` | **阻塞**：执行桥本体 |
| `ITriggerMetadata` | `TriggerMetadata.cs:8` | 32／6 文件 | `Orchestration` 的注册面（`Add(ITriggerMetadata)`、`Triggers`、`TriggersWithHook`、`TriggersWithKey`） | **随 `Trigger` 删**；需先给 `Orchestration` 定替代注册面 |
| `TriggerKind` | `Trigger.cs:29` | 50／20 文件 | 构造参数、`Bus`／挂载合法性（Active 不可挂载） | 随 `Trigger` |
| `TriggerEvent<TView>` | `Trigger.cs` | 13／6 文件 | 构造期事件集合 | 随 `Trigger` |
| `TriggerMetadata` | —— | **0** | **不存在同名类型**：`TriggerMetadata.cs` 只定义 `ITriggerMetadata` 与 `TriggerEventInfo` | **无需处理**（清单中的历史名） |
| `TriggerRegistration` | `Trigger.cs` | 58／7 文件 | `InitialRegistrations` 句柄族 | 随 `Trigger` |
| `TriggerWrapperRegistration` | `Trigger.cs:1281` | 9／1 文件 | `RegisterBefore/After/ReplaceWrapper` 返回句柄 | 随 `Trigger` |
| `TriggerEventInfo` | `Trigger.cs:1313` | 11／3 文件 | `ITriggerMetadata.Events`／`Orchestration.cs:359` 审计投影 | 随 `Trigger` |

**表 C：b5 其余项**

| 类型 | src 引用 | 活体读点 | 裁决 |
| --- | --- | --- | --- |
| `PrefabManager` | 25／14 文件 | `LogicEngine.Prefabs`（公开属性）、`KeywordPrefabLibrary`、`DynamicEffect`、`CardDistributionValidator`；**且**`Packages/Editing/AssetEditorSession.cs`、`Mcp/Tools/AssetTools.cs`、`Packages/AssetMigrationTool.cs` 仍读写 `*.prefab.json` | **阻塞**：不只是执行面，**作者面仍活**（§19.8 第 1 条已实测） |
| `PrefabJson` | 23／14 文件（tests 70／18 文件） | 同上；`PrefabWriter` 反序列化校验、`AssetSchemaMigrator` | 同上 |
| `MatchNamedTriggers` | 5／4 文件 | `Match.cs:697` 建立 → `Player.ConfigureMatchTriggers` → 唯一**读**点 `CardEffectLoader.cs:496`（`injects.target` 按名解析） | **阻塞**：仅服务旧格式 `injects` |
| `LegacyDisplayNames` | 3／1 文件 | `CardMemberTable.cs:106`（10 条）→ 同一条按名解析链 | 同上 |
| `TryFindNamedTrigger` | 3／3 文件 | `CardBase.cs:202`（虚）＋`Hq.cs:73`（覆写），调用点仅 `CardEffectLoader.cs:496` | 同上 |
| `HookRegistry` | 5／4 文件 | `LogicEngine.Hooks` 公开属性 ＋ `HookId.FromName`；**非死代码**：`Trigger.Metadata` 的 hook 名来源 | **随 b4**，本轮不动（删了会让 hook 词表失效，`AuditChainFoundationTests` 4 条红） |
| `AuditGraphBuilder.CollectLegacy*` | **0** | A3 §17.2 已删 `CollectLegacyInjections` | ✅ **已完成**（本轮复验为 0） |

### 19.4 若强行删除，失败形态是什么（为什么不能"先删再说"）

| 删什么 | 立刻发生什么 | 性质 |
| --- | --- | --- |
| `Effect` | `KeywordComponent.EmbedEffect(Effect)`、`Card._effects`、`CardLifecycle.MountEffect` 全部编译失败（142 处）。若为过编译把 `EmbedEffect` 参数换成新基类，等于**给 12 个子类换基类**——那不是删除，是迁移，且迁移完 `Effect` 仍被 `CardEffectLoader` 与 44 个替身持有 | **编译不过**（可回滚，但无进展） |
| 仅删 `PassiveEffect` 的 8 个词条子类 | 若不同时改 `EmbedEffect` 的注入口，装载期拿不到行为对象 ⇒ **被压制／情报／伏击／重甲／免疫／亡计 7 个词条 no-op**，全量仍然全绿（没有任何用例断言"这些词条此刻必须挂着一个 Effect"），**静默** | **静默失效**（最坏） |
| `Trigger<TView>` | `CardMemberTable` 无法物化成员 ⇒ 卡加载抛 `ArgumentException`（成员表是 A1 之后所有卡的执行面）；6 个动作／`OrderFlow`／`CommandManager` 全部编译失败 | **编译不过 ＋ 运行期抛错** |
| `PrefabManager`／`PrefabJson` | `AssetEditorSession`／`AssetTools`／`AssetMigrationTool`／`PackageRegistrar` 编译失败 ⇒ 编辑器与 MCP 资产工具整体下线 | **工具链断裂**（产品面） |

> 关键区别：b5-Bus 能删，是因为 A2 之后它**只是门面**（方法体逐一转发，生产消费者 0）。
> `Effect`／`Trigger` 不是门面，**它们就是被执行的那段代码**。这就是本轮与上轮的分界。

**删除探针实测（本轮硬证据，已完整回滚）**

| 探针 | 操作 | 编译结果 | 恢复 |
| --- | --- | --- | --- |
| b3 | 临时删 `src/Orc/Cards/Effect.cs` → `dotnet build OrcEngine.sln --artifacts-path artifacts/probe-b3-delete-effect` | **46 个 `error CS`／4 个文件**：`DynamicEffect.cs` 16、`Card.cs` 16、`CardLifecycle.cs` 8、`LogicEngine.cs` 6。**下游 5 个项目因 `Orc` 失败不再报错** ⇒ 真实波及面远大于 46 | 从**仓库外**备份还原（两文件本来就是工作区已改状态，不用 `git checkout`），md5 `602fc30165cbbb169b967fe13f53b9bb` 前后一致 |
| b4 | 临时删 `src/Orc/Core/Trigger.cs` → 同上（`--artifacts-path artifacts/probe-b4-delete-trigger`） | **112 个 `error CS`／10 文件**：`DynamicEffect.cs` 36、`Effect.cs` 30、`OrderFlow.cs` 12、`CardLifecycle.cs` 8、`LogicEngine.cs` 6、`Prefabs.cs` 6、`TriggerMetadata.cs` 4、`Orchestration.cs` 4、`Context.cs` 4、`AuditChainJson.cs` 2 | 还原后 md5 `01fc3a490f10e5cb6826870fa85a616f` 一致 |

恢复后复编译：`dotnet build OrcEngine.sln --artifacts-path artifacts/probe-restore` → **0 个错误**（7 个既有 nullable 警告）。
⇒ 两个探针**没有在仓库留下任何残留**，全量基线仍是 2618。

### 19.5 本轮实际改动与被删测试清单

**源码改动：0 行。测试改动：0 条（新增／改写／删除／跳过 均为 0）。**

**被删测试：0 条。** 按本轮给定的三级处置原则，44 个替身逐条落在哪一级：

| 处置级别 | 适用数 | 说明 |
| --- | --- | --- |
| ① 改写成等价新机制断言 | **0** | 新机制（组件边／句柄）目前**不承载**这些用例的被测行为：装载钩子失败回滚、托管清理、`injects` 段次序——v4 面没有对应读点可断言（不是"观测面变了"，是"没有观测面"） |
| ② 保留测试名／意图、只换断言 | **0** | 同上：被测行为本身（"Effect 装载链在失败时回滚为未生效"）在删除后**不存在**，换断言即恒真 |
| ③ 删除（新模型无对应概念） | **0** | 现在删＝在 `Effect` 仍活着时删掉它的验收套件，属"删测试换绿"（R7 禁止）；应在第 3 步（§19.7）之后逐条判定 |

> 也就是说：**44 个替身的处置不能先于 src 迁移**。上轮"停在这里"是对的，本轮复核后仍然是同一结论，只是把
> "为什么"从"改写会恒假"升级为"改写对象尚不存在"。

### 19.6 完成判据实测（三条 grep）

```text
grep -rn "new Trigger<" src/            → 38   ✗（17 文件；全部为活体管道/物化点，§19.3 表 B）
grep -rn "class Effect\b" src/          → 1    ✗（src/Orc/Cards/Effect.cs:52；另有 12 个活体子类）
grep -rn "\bBus\b" src/ --include=*.cs  → 0    ✓
grep -rn "CollectLegacy" src/           → 0    ✓（A3 已完成）
```

**`\bBus\b` 的一处口径修正**：不带 `--include=*.cs` 时全 `src/` 命中 **26 处**，全部是
`src/**/bin/Debug/net8.0/Orc.dll|pdb` 里的**旧编译产物**（源码早已为 0）。判据应带 `--include=*.cs`，
或先 `dotnet clean`；否则会误判成"Bus 没删干净"。本轮已在 header 记明。

### 19.7 解锁路径（两个决策门 ＋ 5 步可执行顺序）

**决策门 A（必须先裁决，否则第 1 步无法开工）——词条行为承载口径**（承接 §18.8 第 2 条）
- **A1：数据壳 ＋ 静态 handler**（本文件**已有先例**：`MobilizeAccrualEffect`／`MobilizeLossEffect`／`BlitzDeployEffect`
  已是静态行为类，由 `keyword.*.prefab.json` 的 `assemblyKey` 引用，`KeywordEffects.cs:40-46` 自述该形态）。
  优点：不引入第二套生命周期；缺点：3 个伤害改写类需要**注册进 `AttackDamageTrigger`** 的时机（当前由
  `KeywordDamageRewriteEffect.OnMount` 承担），b4 未完成前仍要拿 `Trigger<TView>` 句柄。
- **A2：csx 组件**（真正 v4）。优点：一次到位；缺点：需先证明 v4 handler 能拿到 `Judicators`／`DamageAction`／
  `DeathAction`（判定器通道），且 `view.Host is Card` 转换语义要保持（`implementation.md:225`）。

**决策门 B——旧格式资产是否退役**（承接 §18.8 第 1 条）。本轮把它从"待确认"变成**已实测**：
`AssetEditorSession.cs:1596`／`:1622`、`AssetTools.cs:45`、`SandboxEnvironment.cs:202`、`AssetMigrationTool.cs:171`、
`PackageRegistrar.cs:428` 仍在**读写** `*.prefab.json` ⇒ **作者面未退役，`PrefabManager`／`PrefabJson` 不能删**，
除非同批把编辑器／MCP／迁移工具切到 v4 组件目录。

**5 步顺序（每步一次全量，带 `--artifacts-path`）**

| # | 步骤 | 前置 | 验收 |
| --- | --- | --- | --- |
| 1 | 7 个词条行为迁「数据壳＋静态 handler」（`KeywordEffects.cs` 8 个类去 `Effect` 基类 ＋ `DeathrattleEffect`） | 决策门 A | 7 个词条的行为断言**逐条不变**（`KeywordEffectBatch1..4Tests`／`KeywordBatch2Tests`／`DeathrattleAndRetriggerTests` 原样绿） |
| 2 | 旧格式 effect 装载下线：`CardEffectLoader` 的 `DeclarePrefab→Effect` 分支、`Card.Effects`／`MountPassiveEffects`、`EffectRuntime.DynamicEffects`、`EffectDynamicResults` | 1 ＋ 决策门 B（执行面可先退，作者面留） | 12+ 旧格式用例的处理方案先定（改写或删除，逐条列明） |
| 3 | 44 个替身逐条按三级原则处置，然后删 `Effect`／`PassiveEffect`／`ActiveEffect`／`DynamicEffect*`／`EffectInjection`／`ISerializableEffect` | 2 | `class Effect\b`＝0、`\bEffect\b` src＝0 |
| 4 | 卡／指令／流程执行桥迁 v4 成员：`CardMemberTable` 物化、6 个动作内部管道、`OrderFlow` 4、`CommandManager` 7 → 删 `Trigger` 家族 8 个类型 | 3 | `new Trigger<`＝0、`Trigger<` src＝0 |
| 5 | 工具链切 v4 后删 `PrefabManager`／`PrefabJson`／显示名链／`HookRegistry` | 4 ＋ 决策门 B | `PrefabJson` src＝0 |

### 19.8 重构完成后仍需人工确认的事项（更新 §18.8，第 1 条已从"待确认"变为"已实测"）

1. **旧格式资产在编辑器／MCP／迁移工具里是否还需要读**——**实测：需要，且是写入路径**。
   `AssetEditorSession.cs:1596/1622`（提交与路径生成都只认 `*.prefab.json`）、`AssetTools.cs:45`（MCP 写资产工具）、
   `SandboxEnvironment.cs:202`（沙盒扫描）、`AssetMigrationTool.cs:171`、`PackageRegistrar.cs:428`。
   ⇒ 删 `PrefabJson` 的**硬前置是这些工具切 v4**，不是"执行面不用了"。
2. **词条行为承载口径（决策门 A）**：A1（同批 4 先例）还是 A2（csx 组件）。影响 7 个内建词条与判定器通道可达性。
3. **v4 组件是否需要 `Serializable` 概念**：`ISerializableEffect` 删除后，`Orchestration.cs:337` 的
   `Serializable` 字段要么恒真、要么删除；对外快照若依赖该字段需人工确认。
4. **`Trigger.Metadata` 删除后的 hook 名来源**：`HookRegistry` 是 `HookId.FromName` 的写入端，
   删它要连带决定 `AuditChainFoundationTests` 4 条用例的去留（它们测的是"名字↔标识双向互转"这一**行为**，不是机制）。
5. **契约基线重生成**（承接 §18.8 第 3 条，仍未做）：本轮无类型增删，快照不受影响；但第 3／4 步落地时必须重生成。
6. **`Orchestration` 注册面的替代设计**：`ITriggerMetadata` 是 `Add()`／`Triggers`／`TriggersWithHook`／`TriggersWithKey`
   的参数类型；b4 落地时要先定"v4 成员如何被注册进编排实体"，否则这 32 处会变成无处可去的类型参数。

## 二十、A2 执行记录：7 个内建词条迁 v4 效果组件（csx）＋判定器／动作面通道验证

> **一句话结论**：决策门 A 按 **A2** 落地——7 个内建词条的行为**不再由 `Effect` 实例承载**，
> 改为 `src/Orc.Game/Cards/KeywordPrefabs/*.component.json`（v4 组件：成员 csx ＋ 挂载/包装声明）＋
> 词条绑定（`BuiltInPlugin`）；`KeywordEffects.cs` 里 7 个 `Effect` 家族类**全部删除**，
> 文件只剩 3 个静态行为类（旧数据壳样本的行为引用目标，不继承任何基类）。
> `Effect` 家族活体子类从 **12 → 6**（`\bEffect\b` src 引用 142 → 144 里含新增注释；文件数 142 → 40），
> **全量 2620 通过 / 0 失败 / 0 跳过**（基线 2618 → 2620，净增 2 条；`artifacts/test-verify-gateA2`）。
> 注：首轮全量曾报 `Orc.Manager.Tests` 3 红，单独跑该项目 **20/0/0**、复跑全量 **2620/0/0** ——
> 判为**并发／进程级偶发**（Manager 测试是进程级、可能与残留进程冲突），非代码回归。复现方式：单跑该 csproj。

### 20.1 落地形态（一张表看完）

| 词条 | 组件资产 | 行为接入点 | 旧实现 |
| --- | --- | --- | --- |
| 重甲 | `keyword.armor` | `wraps: engine#combat-damage`（before） | `ArmorReductionEffect`（`Effect` 子类） |
| 免疫（单位侧） | `keyword.immune` | `wraps: engine#damage-apply`（before） | `ImmuneZeroingEffect`（`Effect` 子类） |
| 免疫（HQ 侧） | —— | `ImmuneKeywordComponent.Mount` 挂 `HqDamageRewriter` | 同上（HQ 分支） |
| 伏击 | `keyword.ambush` | `wraps: engine#attack-damage`（before） | `AmbushRewriteEffect`（`Effect` 子类） |
| 被压制 | `keyword.suppression` | `mounts: engine#turn.end` ＋ 私有字段 `firstOwnerTurnEnd` | `SuppressionLifecycleEffect`（`Effect` 子类） |
| 情报 | `keyword.intelligence` | `mounts: engine#card.played` | `IntelligenceTriggerEffect`（`Effect` 子类） |
| 动员（累积） | `keyword.mobilize.accrual` | `mounts: engine#turn.start` | `DynamicPassiveEffect`（数据壳＋assemblyKey） |
| 动员（失去） | `keyword.mobilize.loss` | `mounts: engine#card.damaged` | 同上 |
| 闪击 | `keyword.blitz.deploy-set` | `mounts: engine#unit.deployed` | 同上 |

制品落点＝`src/Orc.Game/Cards/KeywordPrefabs/`（既有 `*.prefab.json` 同目录、不同文件模式 `*.component.json`；
`Orc.Game.csproj` 的复制规则已覆盖，无需新增构建项）。装载＝`KeywordComponentLibrary.LoadInto`
（**`Match` 构造期**装进编排运行时目录，早于 `Initialize` 的绑定核验与任何授予）。

### 20.2 三条新增接缝（为什么必须加，以及各自的可删条件）

| 接缝 | 位置 | 存在理由 | 可删条件 |
| --- | --- | --- | --- |
| **动作端接缝**（`KeywordActionEndBridge`） | `Cards/KeywordActionEndBridge.cs` ＋ `OrchestrationRuntime.Assembled`（装配完成事件）＋ `ComponentAttachment.OnDispose`（随实例卸载的清理动作） | 动作端（`combat-damage`／`damage-apply`／`attack-damage`）在 U5 之前由动作实现**直接调用旧身份触发器**（`Trigger<TView>.InvokeAsync`），**不经过编排实体的成员执行面**——组件资产里针对这些成员的 `wraps` 在运行期不会被调用（装载门只做静态规划）。接缝在装配完成时补一条**外来挂载边**，handler 只做一件事：把同一个 `Context` 交给组件成员执行（`OrchestrationEntity.InvokeAsync(string, Context, ct)`），数据面同一份，csx 的 `view.Amount`／`view.IsZeroed` 写回照常落到动作端视图 | b4（动作端改经成员执行面）落地后整类删除 |
| **动作面门面**（`KeywordEffectActionFace`） | `Cards/KeywordEffectActionFace.cs` | csx 需要「改写方自己执行死亡」——旧实现经 `KeywordLoadContext.DeathAction`；`EffectRuntime` 是**契约冻结面**（`ContractFreezeTests` 逐条比对基线），每加一条动作面都变成一次契约变更。本类不属冻结面任何一段，是动作面的稳定落点 | 冻结面策略变化时可合并回 `EffectRuntime` |
| **内建组件库装载**（`KeywordComponentLibrary`） | `Cards/KeywordComponentLibrary.cs` ＋ `Match` 构造期一行 | 内建词条没有"卡包"可挂靠：绑定核验（`VerifyBindings`）与授予链首选路径（`TryAttachComponent`）都以**运行时目录**为判据，故必须在构造期就把 8 个组件资产装进目录 | 内建词条改为随卡包分发时可删 |

**动作端接缝的位次等价性**：接缝用 `Trigger.RegisterMounted`（外来挂载边）注册——与旧效果
`KeywordDamageRewriteEffect.AttachTo` 的注册位次**同一条边类型**；事件名沿用旧显示名
（成员名＝`重甲减伤`／`免疫归零`／`伏击改写`），因此 `EdgePositionMigrationTests.重甲与免疫_改写位次为外来边且在自有边之前`
**逐条不变**（它读的就是 `Events[].Name` 与 `Position`）。

### 20.3 判定器通道与动作面通道（本轮第 2 项）

| 通道 | csx 内的取法 | 可达性证据（可复跑） |
| --- | --- | --- |
| 判定器：`combat.counter.eligibility`／`combat.ambush.condition` | `EffectRuntime.Judge(name, args)`（既有公开面，未改） | `JudicatorCombatCounterAmbushTests`「调用点二（伏击资格）」：同一对局内把资格包装成恒真 → 伏击改写从"不成立"变"成立"——只有 csx 真走判定器注册表才会随动（8 条全绿） |
| 死亡动作（完整两端） | `KeywordEffectActionFace.DeathAsync(host, target, killer, triggerCard, ct)` | 新增 `KeywordV4ComponentTests.伏击改写_死亡经死亡动作完整两端_免死守卫可拦下改写后果`：死亡处理端外挂免死守卫 → 守卫被命中且载荷单位＝被改写的攻击者，改写后果被拦下 |
| 词条参值（运行期读取） | `KeywordRules.GetKeywordValue(host, id)` | `KeywordEffectBatch2Tests.Armor_Reduction_Reads_Value_At_Settlement_Time`（改写 3／置空 → 减伤随动）逐条不变 |
| 伤害写回（数据面） | `view.Amount`／`view.IsZeroed`（`ViewCarrier` 写回同一份 `Context.Data`） | 重甲／免疫的全部真实战斗用例（Batch2 6 条） |

> 反例留档：本轮**没有**用 `wraps` 直连动作端当"看起来 v4"的证据——第一次实跑就证明它不执行（见 §20.2 第一条），
> 这正是接缝存在的理由。**不要再把"资产里写了 wraps"当成"运行期生效"**。

### 20.4 行为等价（逐词条 → 谁在证）

| 词条 | 行为断言（原样绿，测试名） |
| --- | --- |
| 动员 | `KeywordEffectBatch1Tests.Mobilize_Effects_Load_On_Grant_And_Accrue_At_Owner_Turn_Start`／`Mobilize_Loss_On_Damage_Through_Signal_Keeps_Accrued_Bonus`／`Mobilize_Not_On_Field_Skips_Accrual`／`Mobilize_Behavior_Follows_Effect_Detach_And_Reattach_Independently`／`Mobilize_Revoke_Then_ReGrant_Restores_Accrual_Keeping_Bonus`／`Mobilize_Death_Registration_Kept_And_Behavior_Revoked`；`KeywordEffectBatch4Tests.生产装配_行为引用与库键就绪_数据效果端到端生效` |
| 被压制 | `KeywordEffectBatch1Tests.Suppression_Decrement_Follows_Rewritten_Value`／`Suppression_Effect_Follows_Grant_Revoke_And_Reapply`／`Suppression_Death_Revokes_Behavior_Keeping_Registration` |
| 情报 | `KeywordEffectBatch1Tests.Intelligence_Trigger_Follows_Value_And_Effect_Attachment`／`Intelligence_Death_Revokes_Behavior_Keeping_Registration` |
| 伏击 | `KeywordEffectBatch2Tests.Ambush_Rewrite_Follows_Effect_Detach_And_Reattach`／`Ambush_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant`／`Ambush_Death_Revokes_Behavior_Keeping_Registration`；`JudicatorCombatCounterAmbushTests` 8 条 |
| 重甲 | `KeywordEffectBatch2Tests.Armor_Reduction_Follows_Effect_Detach_And_Reattach`／`Armor_Reduction_Reads_Value_At_Settlement_Time`／`Armor_Death_Revokes_Behavior_Keeping_Registration_And_Value`；`EdgePositionMigrationTests.重甲与免疫_改写位次为外来边且在自有边之前` |
| 免疫 | `KeywordEffectBatch2Tests.Immune_Zeroing_Follows_Effect_Detach_And_Reattach`／`Immune_Death_Revokes_Behavior_Keeping_Registration`／`Immune_Hq_Rewriter_Follows_Grant_And_Revoke` |
| 闪击 | `KeywordEffectBatch3Tests.Blitz_Set_Follows_Effect_Detach_And_Reattach`／`Blitz_Behavior_Revoked_On_Keyword_Revoke_And_Restored_On_Regrant`／`Blitz_Death_Revokes_Subscription_Keeping_Registration`／`Blitz_Runtime_Grant_Revoke_Closure_Follows_Through_Deployment`／`Blitz_MultiUnit_Deployment_Does_Not_Cross_Set_Flags`；`KeywordEffectBatch4Tests.生产装配…端到端生效` |

**测试名与行为断言（数值、状态、信号次序）逐条未改**。改的只有"承载物观测面"与三处**语义确实变化**的断言（§20.5）。

### 20.5 测试改动逐条（三条硬必须改，其余是观测面换轨）

**A. 观测面换轨（`Effect` 实例 → 组件实例；断言语义不变）**：`FindEffect(card, "名字")`
→ `FindCarrier(card, 组件 id)`（读 `宿主实体.Runtime.ComponentsOf(entity)`）；`effect.IsMounted` → `attachment.IsAssembled`；
`card.RemoveEffect(effect)` → `attachment.Dispose()`；`card.AddEffect(effect)` → `runtime.AddComponent(entity, id, name)`（复装＝重建实例）。
落在：`KeywordEffectBatch1Tests`（7 处）、`KeywordEffectBatch2Tests`（16 处）、`KeywordEffectBatch3Tests`（8 处）、
`KeywordEffectBatch4Tests`（5 处）。原 `Assert.Empty(card.Effects)` 全部保留（v4 形态下它恒真空，故同时补一条"组件不在列"）。

**B. 三处语义确实变化（必须改，逐条说明）**

| # | 用例 | 原断言 | 新断言 | 为什么必须改 |
| --- | --- | --- | --- | --- |
| B1 | `KeywordEffectBatch2Tests.Detached_Load_Without_Context_Skips_Registration_And_Loads` | 无上下文卡上**有**三个效果实例且 IsMounted | 三个组件承载**都不在**（零承载、无半态） | 旧形态"效果实例装载但不订阅"；v4 形态的装配点就是"有上下文"，无上下文＝不装配。功能不可用的黑盒结论一致（不订阅、不置位），可观测面不同 |
| B2 | `VeteranMechanismTests.Clear_Special_Purges_…` 与 `Failure_Execution_Rolls_Back_…` | 词条全清时发 `effect.removed` 恰 2 条（动员双效果卸载） | `effect.removed` 恰 **0** 条；其余信号与次序断言不变 | 组件不是效果：`effect.removed` 是效果容器的完成通知，`ComponentDisposalPolicy` 明令它不能作处置信号。**这是可观测信号面的净减少**（若要保留，需要在组件卸载时额外发一条非 effect 语义的通知——见 §20.8 待确认第 1 条） |
| B3 | `EdgePositionMigrationTests`（未改）／`KeywordEffectBatch4Tests.词条效果库_单文件损坏_…` | 用生产绑定充当"坏库的引用者" | 临时登记一条只指向库目录内 csx 样本键的绑定 | 生产 7 个词条的绑定已切 v4 组件（`VerifyBindings` 按"v4 目录 或 旧库"二选一通过），旧的"缺库键"不再出现——不是放宽，而是引用对象换了 |

**C. 绑定恢复目标**：`KeywordEffectCsxTests.SwitchMobilizeBindings(false)` 的"恢复"目标改为**生产实际绑定**（v4 组件 id）。
不改＝把进程级注册面留在旧形态，污染后续套件。

### 20.6 全量阶梯（每层一次全量，均带 `--artifacts-path`；常驻 `Orc.Manager` 未杀）

| 层 | 触发 | 结果 |
| --- | --- | --- |
| 基线（起点复验） | 开工前 | 2618 / 0 / 0 |
| F（聚焦层，逐词条） | 每次迁移后跑 `Keyword*`／`Effect*`／`Damage*`／`Veteran*`／`Edge*` 子集（572 条） | 由 5 红 → 2 红 → 0 红 |
| **L1（全量）** | 7 词条全部迁完、测试改签完成 | Orc.Game 1470/2 红（`EdgePositionMigrationTests` 名称位次、`ContractFreezeTests` 契约面）＋其余 7 个程序集全绿 |
| **L2（全量）** | 两处修正后（事件名沿用旧显示名；`DeathAsync` 移出冻结面） | **2620 / 0 / 0**（`artifacts/test-verify-gateA2`；首轮曾有 3 条 Manager 偶发红，见本节顶部注） |

> 说明：L1 的两红都属"我加的接缝留下的痕迹"，不是行为回归：
> ①接缝最初用 handler 资产 id 当事件名 → 审查面名字变了；改回旧显示名后逐条不变；
> ②`EffectRuntime.DeathAsync` 是**公开成员**，落进契约冻结面 → 改为 `internal` ＋ `KeywordEffectActionFace`（非冻结面）后契约面零 diff。
> **契约基线本轮未重生成**（`ContractFreezeTests` 通过即证明冻结面无变更）。

### 20.7 旧格式 effect 装载下线（第 3 项）：实测边界与探针结果

本轮把"词条侧"的旧格式路径**清零**了（7 个词条不再有任何 `Effect` 实例），但**第 2 步整段下线做不到**，
原因不是"测试替身"，而是执行面仍有活体消费者。三张底数（可复跑）：

| 度量 | 改动前 | 改动后 | 命令 |
| --- | --- | --- | --- |
| `class Effect\b`（src） | 1 | 1 | `grep -rn "class Effect\b" src/ --include=*.cs` |
| `Effect` 家族活体子类（src） | 12 | **6** | `grep -rnE ":\s*(PassiveEffect\|ActiveEffect<\|Effect\b)" src/ --include=*.cs` |
| `\bEffect\b` 引用（src） | 142／40 文件 | 144／40 文件 | `grep -rnw "Effect" src/ --include=*.cs` |
| `DeclarePrefab` 调用点（src／tests） | 4／30（8 文件） | 未动 | `grep -rn "DeclarePrefab" src/ tests/` |
| `Card.Effects` 使用（tests） | 290／64 文件 | 未动 | `grep -rn "\.Effects\b" tests/ --include=*.cs` |
| `AddEffect`／`RemoveEffect`／`MountPassiveEffects`（tests） | 196 | 未动 | 同上 |

**探针（本轮实做，已完整回滚）**

| 探针 | 操作 | 结果 | 恢复 |
| --- | --- | --- | --- |
| P1 | 删 `CardEffectLoader` 的 ③b「`DeclarePrefab→Effect`」分支 ＋ `DeclarePrefab`／`PrefabDeclarations`／`CardEffectLoadContext.PrefabDeclarations` | **<P1> 个 `error CS`／<P1F> 文件**（主要在 tests：`PrefabEffectLoadingTests`／`EffectMechanismEndToEndTests`／`KeywordEffectChannelTests`／`EffectRuntimeEndToEndTests`／`CardToolchainEndToEndTests`…）；src 侧 `CardPackAssembler.cs:94`、`MatchCardService.cs:205`、`McpService.MatchLaunch.cs:117` 编译失败 ⇒ **旧格式包（`effects/`）整体无法装载** | 从仓库外备份还原，md5 前后一致 |
| P2 | 删 `Card.Effects` 公开读面（连同 `MountPassiveEffects` 公开面） | **<P2> 个 `error CS`／<P2F> 文件**（src：`Orchestration.cs`、`CardEffectLoader`、`EffectRuntime.DynamicEffects`、`CardCastActions`、`KeywordServices`…；tests：64 文件） | 同上 |

> **结论**：第 2 步的硬前置是**先把"旧格式效果"这条产品路径搬走**——不是搬走执行面，而是搬走**资产作者面与样例包**：
> `samples/card-pack-{diy,demo,across-the-pacific,space-war}` 仍以 `effects/*.prefab.json` 为原生形态（且 `card-pack-diy` 同时有 v4 目录＝迁移中间态），
> 编辑器／MCP／迁移工具仍在**写** `*.prefab.json`（§19.8 第 1 条已实测）。在这之前删装载分支＝删掉这些包与它们的验收套件（R7 禁止）。
> `EffectRuntime.DynamicEffects`（`AttachPrefabAsync`／`CompileAttachAsync`）同理：它是 csx 侧"运行期挂旧格式效果"的受控面，
> 调用方＝DIY 套件与 `EffectRuntimeDynamic*Tests`，与本轮 7 个词条无关。

### 20.8 完成判据实测

```text
grep -rnE ":\s*(PassiveEffect|ActiveEffect<|Effect\b|KeywordSignalEffect|KeywordDamageRewriteEffect)" src/Orc.Game/Cards/KeywordEffects.cs
  → 0（文件只剩 3 个静态行为类，无基类继承）
grep -rn "class Effect\b" src/                                        → 1（src/Orc/Cards/Effect.cs:52；本轮不动类型本体）
grep -c "keyword\..*\.component\.json" src/Orc.Game/Cards/KeywordPrefabs/ → 8 个资产（7 个词条，动员 2 件）
dotnet test OrcEngine.sln --artifacts-path artifacts/test-gateA-L2    → 2620 / 0 / 0（复验：test-verify-gateA2）
```

### 20.9 `Effect` 剩余消费者清单（文件:处数；供下一步删类型）

按引用计数（`grep -rcw "Effect" src/ --include=*.cs`），**函数体级消费者**加粗（只出现在注释里的不进"必须改"清单）：

| 文件:处数 | 性质 | 删类型时必须处理 |
| --- | --- | --- |
| **`src/Orc.Game/Cards/CardEffectLoader.cs` : 17** | 旧格式装载链（`DeclarePrefab→Effect`、`Effect` 构造与回滚、托管清理） | 是（第 2 步主体） |
| `src/Orc/Cards/Effect.cs` : 12 | 类型本体（`Effect`／`PassiveEffect`／`ActiveEffect`／`EffectInjection`／`ISerializableEffect`） | 是（就是被删对象） |
| `src/Orc.Game/Cards/KeywordManager.cs` : 11 | 内嵌效果通道（`EmbeddedEffects` 遍历、`AttachEmbeddedEffect`、旧格式回退） | 是（词条侧回退随第 2 步下线） |
| **`src/Orc/Core/Trigger.cs` : 10** | 触发器 owner＝`Effect` 时的 `Host` 注入（`_owner is Effect`） | 是（b4 的 owner 泛化） |
| **`src/Orc/Cards/Card.cs` : 9** | 效果容器本体（`_effects`／`AddEffect`／`RemoveEffect`／`MountPassiveEffects`） | 是（第 2 步主体） |
| **`src/Orc/Cards/DynamicEffect.cs` : 8** | `DynamicPassiveEffect`／`DynamicActiveEffect`／`DynamicEffectFactory` | 是（第 3 步） |
| **`src/Orc/Cards/CardLifecycle.cs` : 8** | 装载/卸载链（`MountEffect`／`DisposeEffect`） | 是 |
| **`src/Orc.Game/Effects/EffectRuntime.DynamicEffects.cs` : 8** | `AttachPrefabAsync`／`CompileAttachAsync`／`DetachEffectAsync` | 是（或被"v4 组件版"替换） |
| `src/Orc.Game/Cards/KeywordComponent.cs` : 7 | 内嵌效果通道（`_embeddedEffects`） | 是 |
| `src/Orc.Packages/Editing/AssetEditorSession.cs` : 5 | 作者面（写 `*.prefab.json`） | 否（决策门 B，工具链第 5 步） |
| `src/Orc/Core/LogicEngine.cs` : 3 | 卡销毁/效果卸载调用点 | 是 |
| `src/Orc/Core/AssetIdentity.cs` : 3 | 资产标识（效果 id 归一化） | 视实现 |
| `src/Orc.Game/Effects/EffectRuntime.cs` : 3 | `KillAsync`／`DamageAsync` 文档＋`DeathAsync`（internal） | 否（只改注释） |
| `src/Orc/Output/JsonValueWriter.cs` : 2 | 快照写出（`Serializable` 字段） | 视 §19.8 第 3 条裁决 |
| `src/Orc/Core/Orchestration.cs` : 2 | 审查链 `card.Effects` 采样（nodes 面） | 是 |
| `src/Orc/Core/Model/KernelSignals.cs` : 2 | `effect.removed` 信号常量 | 是（信号词表） |
| `src/Orc/Core/Model/DisposalPolicy.cs` : 2 | 处置策略（`effect.removed` 禁用作兜底） | 是 |
| `src/Orc.Packages/AssetMigrationTool.cs` : 2 | 迁移工具（读旧格式） | 否（第 5 步） |
| `src/Orc.Game/Targeting/Selectors.cs` : 2 | 选靶 `EffectSelector` 命名 | 否（词同名、非类型） |
| `src/Orc.Game/Effects/EffectDynamicResults.cs` : 2 | 动态挂载结果类型 | 是（第 3 步） |
| `src/Orc.Game/EffectParsing/Templates/EffectTemplate.cs` : 2 | 模板（`*.tpl.json`） | 否（解析器资产） |
| `src/Orc.Game/Damage/DamageKinds.cs` : 2 | 注释 | 否 |
| `src/Orc.Game/Cards/ProgramKeywordComponent.cs` : 2 | 词条程序（`buff` 走 `Modifiers`） | 否（注释） |
| `src/Orc.Game/Cards/KeywordEffects.cs` : 2 | 注释（本轮遗留说明） | 否 |
| **`src/Orc.Game/Cards/DeathrattleSystem.cs` : 2** | `DeathrattleEffect : PassiveEffect`（亡计内容承载） | 是（亡计要把"内容"改 v4 组件或成员句柄） |
| `src/Orc.Game/Cards/CardCastActions.cs` : 2 | `Effect`→`ICastAction` 回退分支 | 是（`ActiveEffect` 删除后） |
| 其余 15 个文件 : 1 | 单点引用（`Updates.cs`／`SignalBridge.cs`／`AuditGraphBuilder.cs`／`ICastAction.cs`／`CardLoaderTrigger.cs`／`HandSelection.cs`／`GameHooks.cs`／`EffectCompiler.cs`／`PincerSystem.cs`／`KeywordComponentLibrary.cs`／`DeclarativeKeywordComponent.cs`／`CardBase.cs`／`OrderAction.cs`／`DamageAction.cs`／`AssetMigrationTool.cs`） | 逐点判（多为文档/注释或信号名） |

**tests 侧（下一步的第 3 步对象）**：`Effect` 替身 44 个／21 文件（§19.2 底数不变）；`\.Effects\b` 290／64 文件；
`AddEffect|RemoveEffect|MountPassiveEffects` 196。

### 20.10 仍需人工确认（新增，承接 §19.8）

1. **`effect.removed` 信号面净减少**（§20.5 B2）：词条组件卸载不再发任何"卸载完成"信号。
   若前端/审计依赖这条信号做"效果消失"刷新，需要在 v4 组件卸载点定义一个**非 effect 语义**的通知
   （如 `component.detached`），并同批改前端。**这是本轮唯一需要产品侧裁决的行为面变化**。
   - **裁决（2026-10-27，父 agent）**：**保留 `effect.removed` 作为信号名，语义泛化为"某个效果位消失"**
     （组件卸载与旧效果卸载都发）。理由：①前端与审计已消费该名，改名要同批改前端且断审计链；
     ②"净减少"是可观测行为回归，R7 不允许；③名字里的 `effect` 指**业务语义的效果位**，不是 `Effect` 类型——
     类型删除后该名仍成立。落地要求：`ComponentAttachment.OnDispose` 补发一次 `effect.removed`（载荷带组件 id 与宿主），
     并把 §20.5 B2 的断言从"恰 0 条"改为"恰 N 条且载荷为组件"。**该条不再需要产品侧裁决**。
2. **HQ 侧免疫仍在 C#**（`ImmuneKeywordComponent.Mount` 挂 `HqDamageRewriter`）：HQ 不是 `CardBase`、无编排实体，
   其伤害走自有数值管线（不经 `engine#damage-apply`），v4 组件覆盖不到。**选项**：
   ①给 HQ 建编排实体并新增 `damage.hq.amount` 判定器（推荐，一次性把 HQ 拉进 v4）；
   ②维持现状（组件里一段宿主接线）。本轮取②，因为①会引入新判定器名（契约/文档面扩大）。
3. **内建组件 id 与旧 prefab id 同名**（`keyword.armor` 等 vs 旧 `*.prefab.json` 的 root id）：两个注册面不同
   （`Model.Catalog` vs `Prefabs`），绑定核验按"v4 目录优先"判定。**副作用**：以旧 prefab 键为"必须缺失"的用例
   不再能靠生产绑定取证（§20.5 B3 已改）。若要彻底消除歧义，可把 v4 组件 id 加 `.component` 后缀——需同批改绑定与文档。
4. **`Trigger<TView>` owner 的 `Effect` 分支**（`Trigger.cs:10` 处）：b4 落地时要给"组件实体 owner"的 `Host` 注入
   定等价规则，否则 b4 之后 csx 的 `view.Host` 语义会与现在不同。

## 二十一、b6 门 B 实测：样例包 v4 产物已就绪，但**不能**切为唯一格式（迁移器两处修正 ＋ 逐条阻塞证据）

> **一句话结论**：本轮把迁移器修到"不再造误挂边"，`samples` 的 **20 份 v4 产物全部重生成**（hooks 派生挂载 25 → 23），
> 全量 **2620 / 0 / 0**（`artifacts/test-b6-node1`，8 个程序集：1472+474+372+118+112+39+13+20）。
> 但**第 2 步"把旧 `effects/` 移出产品路径"实测失败，已完整回滚**：5 个样例包里有 3 个包的关键行为在 v4 面**不执行**，
> 原因不在资产，在**引擎生产端**——`engine#action-budget` 这类动作管道成员**只有声明、没有发射**，`host#targeting`
> 这类宿主作用域成员也拿不到旧具名触发器的调用。⇒ 门 B 的硬前置从"搬走资产作者面"改写为
> **"先补生产端发射（U3/U4）"**，而这件事与 **b4（拆 `Trigger<TView>`）是同一件工作的两面**，必须同批做。

### 21.1 本轮真正落地的两处（迁移器；可独立验收）

| # | 修正 | 原判据 | 新判据 | 影响 |
| --- | --- | --- | --- | --- |
| 1 | **占位 hooks 判定**（`AssetSchemaMigrator.CoversRequiredFields`） | 只看"必填字段被信号载荷覆盖" | ①必填覆盖 **且** ②视图与信号载荷**至少一个同名字段** | 全可选视图（`UnitAttackTriggerView`／`ActionQuotaView`）不再被判成真订阅；`engine#card.destroyed → onAttack/onQuota` 两条误挂边消失（卡销毁时会误跑攻击/额度逻辑） |
| 2 | **落盘幂等**（`MigratePackToDisk`） | 无条件 `File.WriteAllText` | 内容逐字节一致＝跳过 | 重跑迁移器不再触碰已正确产物（也避开只读产物上的无谓写失败） |

**为什么第 1 条是"修正"而不是"放宽"**：迁移器原本会把 space-war 的 `强磁护盾·攻击后失去` 挂到 `card.destroyed` 上——
旧资产的 `hooks: ["card.destroyed"]` 是**填充值**（旧链要求 hooks 非空），真实接线在 `injects[].target`（显示名）。
判据①在"视图全可选"时恒真，于是填充值被当成真订阅。加上判据②后：hooks 23 → 挂载 23、占位 5（space-war 一个包 5 个 hooks 全是填充值）。

### 21.2 samples 全量重生成（可复跑）

```text
dotnet run --project tools/Orc.Tools.Migrate -- --schema4 --root samples --apply
→ 包 5｜触发器 22｜handler 24｜csx 改写 24｜hooks 23→挂载 23｜inject Rewrite 4／Commit 1／挂载 2
  ｜判定器包装 2｜失败 0
```

产物：`samples/card-pack-{demo,diy,across-the-pacific,space-war,bulwark}/effects-v4/*.component.json` 共 **20** 份。
装载证据：`MigratedAssetLoadTests.全部迁移产物可装载_并按包原子注册`（`Assert.Equal(20, loaded)`）＋
`迁移产物的成员可实例化并按声明序建边`（wraps 两条：`damage-apply` before／`unit-attack` after）全绿。
⚠️ 这 20 份产物在 git 里仍是**未跟踪**状态（上一轮生成后未提交）；本轮的改动是"重新生成并修正其中的误挂边"。

### 21.3 门 B 实测：把 `effects/` 移走后，哪些行为不执行（逐条，已完整回滚）

做法：`effects/` → `samples/_legacy/<pack>/effects/`，`manifest.metadata` 去掉 effects sidecars，
再把读样例包的测试夹具从 `engine.Prefabs.LoadDirectory(effects/)` 改到 `AssetPackLoader.Load(effects-v4/)`。
结果：**13 红 →（§21.1 第 1 条修正后）11 红 →（§21.6 断言改写后）7 红**，7 条全部落在"信号不到"这一类：

| # | 用例 | 期望 | 实测 | v4 接线 | 判据（可复跑） |
| --- | --- | --- | --- | --- | --- |
| 1 | `SpaceWarKeywordTests.强磁护盾_指挥攻击后失去_本次仍减伤` | 指挥攻击后失去词条 | 词条仍在 | `wraps: engine#unit-attack`（after） | 该成员**只有声明**（`MatchActionCatalog` 引用），无 `Emit("unit-attack")` 调用点 |
| 2 | `SpaceWarKeywordTests.磁反应装甲_伤害归零_被敌方指向扣层_归零移除` | 被指向后扣层 | 层数不变 | `mounts: host#targeting` | 宿主作用域成员：旧「指向触发器」是**直接 `InvokeAsync`**，不经该成员 |
| 3 | `BulwarkKeywordTests.机动_首次攻击免额度消耗_并失去词条` | 首次攻击不扣额度 | 额度被扣 | `mounts: engine#action-budget` | `grep -rn "EngineProcesses.ActionBudget" src/` → **0 处引用**（声明即死） |
| 4 | `BulwarkKeywordTests.机动_奋战_先攻击可打三次且不能移动` | 三次攻击 | 桥接超时 | 同上 | 同上 |
| 5 | `DiyExample05_DiscardFetchTests.战地救护_…` | 弃牌后从卡组抽自身 | 未抽 | `mounts: engine#card.discarded` | 该信号**有**生产发射（`GameUpdates.EmitCardDiscarded`），视图字段亦兼容 ⇒ 根因**未定位**（见 §21.7 第 1 项） |
| 6 | `DiyExample03_DestroyedCountTests.纪奠_…` | 按被摧毁数回复 HQ | 共享变量未写入 | `mounts: engine#card.died` | 同上（`EmitCardDied` 存在），根因**未定位** |
| 7 | `DiyCardPackTests.预检_DIY卡包_双零且统计面正确` | 预检 5 卡＋6 库效果双零 | 库侧读不到 | —— | `CardDistributionValidator` 只认 `*.prefab.json`（`PrefabJson`／`PrefabManager.PrefabFilePattern`），**预检器本身要切 v4** |

**这张表的用途**：它不是"6 个 bug"，而是**一处结构性缺口**的五种表现——生产端还没有"对所有成员发射"，
而 v4 组件的执行面完全依赖发射。第 5、6 条是唯一还没归因的两行，必须在下一轮用"装配后打印 `ComponentsOf` +
`EdgesInto(signal)`"的方式定位（`V4PackInMatchTests` 的写法可以直接复用）。

### 21.4 为什么这同时卡住 b3／b4（对照 §19.7 的五步顺序）

```text
删 Effect（第 3 步）        需要 samples 不再依赖旧格式（否则＝删掉 5 个包与它们的验收套件，R7）
samples 切 v4-only（第 1 步）需要 §21.3 那 7 条行为在 v4 面成立
那 7 条成立                  需要生产端把 unit-attack／action-budget／host#targeting／… 都发射
而"发射"这件事本身            就是 b4（拆 Trigger<TView>）要搬的那段代码（旧触发器是唯一的发射入口）
```

⇒ **三者互为前置，不能被切成三轮**。本轮据此**没有**动第 3–7 步的任何一行源码：
硬删只会得到 `Effect` 编译不过（46 个 CS／4 文件，§19.4 已证）或 v4 面 7 条静默失效。

### 21.5 完成判据实测（四条 grep，与 §20.8 同口径）

```text
grep -rn "new Trigger<" src/ --include=*.cs        → 38   ✗（17 文件，未动）
grep -rn "class Effect\b" src/ --include=*.cs      → 1    ✗（src/Orc/Cards/Effect.cs:52）
grep -rnw "\bBus\b" src/ --include=*.cs            → 0    ✓
grep -rn "PrefabJson" src/ --include=*.cs          → 23 / 14 文件   ✗
Effect 家族活体子类（src）                          → 5（DynamicPassiveEffect／DynamicActiveEffect／
                                                          PassiveEffect／ActiveEffect<TView>／DeathrattleEffect）
Trigger< 引用                                       → 186 / 37 文件
```

### 21.6 测试改动（0 删除、0 跳过；1 条断言改写）

| 文件 | 改动 | 理由 |
| --- | --- | --- |
| `tests/Orc.Game.Tests/MigratedAssetLoadTests.cs` | `Assert.Contains(Handles, Edge.Source == Explicit)` → `Assert.DoesNotContain(…)` | 该条原本等的正是 §21.1 第 1 条修掉的那条**误挂边**（`engine#card.destroyed → onAttack`）。新断言直接表达"不再造填充挂载"，同一用例下方的 wraps 断言（`damage-apply`／`unit-attack`）逐条不变 |

其余测试**未改**：`effects/` 回滚后，读样例包的旧用例（`SpaceWarKeywordTests`／`BulwarkKeywordTests`／`DiyExample*`／
`CardPackDemoTests`）全部回到原状并全绿。**本轮的 v4 夹具改造（`V4PackFixture`）已删除**，不留半成品。

### 21.7 下一轮开工清单（按此顺序，逐项可验收）

1. **定位 §21.3 第 5、6 条**（`card.discarded`／`card.died` 为什么到不了组件）：复用 `V4PackInMatchTests` 的取证写法
   （`Catalog.Contains` → `ComponentsOf(entity)` → `EdgesInto(signal)`），先证"边在不在"，再证"handler 跑没跑"。
   这两条若成立，diy 包可以先单独切 v4-only（作为第一个样板包）。
2. **补生产端发射**（U3/U4，与 b4 同批）：`action-budget`／`unit-attack`／`host#targeting` 等——
   落点就是现在 `Trigger<TView>.InvokeAsync` 的调用点，替换为 `engine.Emit("<成员名>", payload)`；
   旧触发器在同一处保留（双面并存，`V4PackInMatchTests` 已证明双面可共存且新面先跑）。
3. **`CardDistributionValidator` 切 v4**（`PrefabJson` → `AssetSchema.Read`，库目录认 `*.component.json`），
   这是 §21.3 第 7 条，也是 `PrefabJson` 删得掉的前置之一。
4. 回到 §19.7 五步：样例包 v4-only → 旧装载链下线 → 删 `Effect` → 删 `Trigger` → 删 b5 其余。
5. **工具链（本轮未动）**：`AssetEditorSession`（`Commit` 只认 `*.card.json`／`*.prefab.json`；`DefaultSourceFor(Effect)`
   生成 `effects/<id>.prefab.json`）、`AssetTools`、`SandboxEnvironment`（脚手架建 `effects/`、列 `*.prefab.json`）、
   `PackageRegistrar`（旧 prefab 反序列化）——**豁免**：`AssetMigrationTool` 必须保留旧格式读取（它就是迁移器）。

### 21.8 重构完成后仍需人工确认的事项（最终版，承接 §20.10）

**父 agent 裁决（2026-10-27）**

| # | 事项 | 裁决 |
| --- | --- | --- |
| 1 | 门 B 顺序反转：先做生产端发射（U3/U4）再切 v4-only | **接受**。U3/U4 与 b4 合并成一轮（不可再分，§21.3 已证）。§19.7 的顺序以本条为准 |
| 2 | `effect.removed` 泛化未落地 | **必须落地**，且是删 `Effect` 的前置。`ComponentAttachment.OnDispose` 补发带组件 id 的 `effect.removed`，B2 断言从"恰 0 条"改为"恰 N 条且载荷为组件" |
| 3 | `samples/*/effects-v4/` 20 份未跟踪 | **随本轮提交**。它们是产品资产，不是生成物；CI 里不该依赖"先跑迁移器" |
| 4 | HQ 侧免疫仍在 C# | 维持现状（`damage.hq.amount` 判定器属独立议题，不阻塞本次） |
| 5 | 内建组件 id 与旧 prefab id 同名 | 维持现状（两个注册面；绑定核验按 v4 优先） |
| 6 | 契约基线 | 删 `Effect`／`Trigger` 落地时**必须重生成**（`ORC_WRITE_BASELINE=1` + `BaselineGeneratorTests`） |

## 二十二、b4 执行记录：生产端发射接线 ＋ `effect.removed` 泛化（U3/U4 与 b4 合并的一轮）

> **进行中**。本轮按父 agent 裁决（§21.8）把顺序反转：**先补生产端发射，再切 v4-only**。
> 本节随执行逐步补齐；已完成的条目都带可复跑的测试名与实测数字。

### 22.1 第一件事：§21.3 第 5、6 条的根因（已定位，两条都是**真缺陷**，不是"发射缺失"）

取证方法：`_ProbeB4Tests`（临时探针，提交前删除）按**生产链路**（`PackageRegistrar` → `CardPackAssembler` → `Initialize`）
装 `samples/card-pack-diy`，然后打印 `Model.Components`（宿主／组件 id／`IsAssembled`／句柄列表）＋
`BuildAuditGraph().EdgesInto(signal)`，再真跑场景。

**第一次取证结果（结论与 §21.3 的猜测相反）**：

```text
component: host=战地救护#1 id=diy.field-rescue.effect assembled=True
   edge -> engine#card.discarded member=onDiscarded
   edge -> engine#card.destroyed member=framework.dispose
edges into card.discarded = 1   (diy.field-rescue.effect / onDiscarded / gap=False)
after discard: deck 7 -> 7; inHand=False          ← 边在、gap=False、handler 就是不生效
```

⇒ "边没建"和"信号没发"两个假设都被证伪：**边建了、信号也发了、handler 也进了，是它在 handler 内部早退**。
继续二分，得到两个互相独立的根因：

| # | 根因（真实缺陷） | 证据 | 修法 |
| --- | --- | --- | --- |
| 1 | **框架注销边缺宿主判定**：`framework.dispose` 挂在 `engine#card.destroyed` 上，而那是**引擎级共享成员**——任何一张卡被销毁，全体组件的注销边都会执行。`DisposeFromFramework()` 无条件 `Dispose()`，于是"一张无关卡牌死亡＝所有卡上的组件被卸载" | 探针：`DumpComponents` 之后、弃掉一张**无关**步兵之后，按 id 再查组件已 `Sequence contains no matching element` | `DisposeFromFramework` 先按载荷判定（口径与旧 `Effect.MatchesDisposalPayload` 逐项同源：`Effect`／`Card`／`Unit` 出现则按引用相等比对宿主，三者皆缺＝照旧注销）；**不能用 `ctx.Stop()`** —— 那会停掉整个共享成员、把别人的注销一起吞 |
| 2 | **发射路径不建执行帧**：`SignalBridge.EmitAsync` 直接 `new Context(...)` 就 `ExecuteMemberAsync`，没有 `ExecutionFrame` ⇒ `ctx.Engine` 恒 null、`ctx.Stopped` 恒 false、`ctx.Interrupted` 恒 false | 探针：修掉根因 1 后 `card.discarded` 通了（`deck 7 -> 6; inHand=True`），但 `card.died` 的计数仍为 0——该 csx 第一行就是 `if (ctx.Engine is null) return;` | `EmitAsync` 在成员执行段建帧（父帧＝发射时所在会话），并 `ctx.AttachFrame` |

**修后实测**（同一探针）：

```text
counter after 2 kills = 2        ← 原 0（根因 1＋2 叠加）
after discard: deck 7 -> 6; inHand=True   ← 原 7 -> 7 / false（根因 1）
```

**这条为什么重要（影响面远超两条用例）**：迁移后的 csx 普遍写
`var engine = ctx.Engine; if (runtime is null || engine is null) return;`
（`atp.*` 四个组件、`diy.memorial` 两处）——根因 2 让**所有**这类 handler 静默空转，
而审查面（边、gap、位次）全部正常，所以任何静态检查都发现不了。

### 22.2 A 项：`effect.removed` 泛化（组件卸载对订阅方可见）

| 落点 | 改动 |
| --- | --- |
| `Orc.Cards.PayloadKeys` | 新增 `Component`／`ComponentInstance`／`Host` 三个键（与 `Effect` 键**互斥**，见下） |
| `OrchestrationRuntime.SignalEmitter` | 新增**发射通道**（`internal` 委托；`LogicEngine.Model` 的 setter 在挂载时接线 `Emit`）。模型层不持有 `LogicEngine`，故用单向委托注入；未接线＝不发射（纯内核／单测直接构造运行时零副作用） |
| `ComponentAttachment.Dispose` | 逐句柄撤销 → 清理动作 → 销毁组件实体 → **补发一次** `effect.removed`（幂等：`_disposed` 守卫之上，重复 Dispose 不重复发） |

**载荷设计为什么不含 `Card`／`Effect` 键**（这是本节唯一需要论证的点）：
唯一在产的 `effect.removed` 消费方是 `CardLoadoutProcessor.OnCleanup`——它做两件事：
①`view.Effect as Effect` 命中 ⇒ 效果销毁兜底；②否则 `view.Card as Card` 命中 ⇒ **摘掉卡牌登记**（`UnregisterCard`）。
组件卸载两者都不是：带 `Card` 会让"词条被移除"直接把仍存活的卡从 `LogicEngine.Cards` 登记里摘掉（真实风险，非理论）。
故组件形态只带 `Component`／`ComponentInstance`／`Host` —— 旧链两个分支都落空 ⇒ 幂等无操作，
不需要在新链路上加"是不是组件"的判断分支（键集互斥即判别式）。

**B2 断言改写（§20.5 B2，§21.8 第 2 条裁决）**：`tests/Orc.Game.Tests/VeteranMechanismTests.cs` 两条

| 用例 | 原断言 | 新断言 | 实测 N |
| --- | --- | --- | --- |
| `Clear_Special_Purges_…` | `effect.removed` 恰 **0** 条、总信号 2 条 | 恰 **2** 条且载荷为组件（`Component` 为 string＋`Host` 引用相等＝被升级单位＋**不含** `Effect`／`Card` 键）、总信号 4 条 | 2（动员＝accrual＋loss 两个组件） |
| `Failure_Execution_Rolls_Back_…` | `effect.removed` 恰 0 条、`Updates` 全空 | 恰 **2** 条且载荷为组件；**除它之外零信号**（`DoesNotContain(u => u.Type != EffectRemoved)`） | 2（词条集全清卸掉两份组件，回滚段重建） |

> 这两条不是"放宽"：语义从"组件卸载不可观测"变成"可观测且可判别为组件"，断言更强（多了载荷形状三条）。
> 为什么 N 都是 2：动员在内建词条库里是**两个**组件（`keyword.mobilize.accrual`＋`keyword.mobilize.loss`），
> 与 §20.5 记的"动员双效果卸载"是同一数字——迁移前后数量不变。

### 22.3 B 项：生产端发射接线（三个成员，逐个给发射点）

统一落点＝`src/Orc.Game/Model/ModelMemberDispatch.cs`（新增）：`InvokeEngineMemberAsync`（`engine#…`）／
`InvokeHostMemberAsync`（`host#…`）。**为什么不是 `Emit`**：`Emit` 是信号面（写条目＋通知观察者＋要求目标是信号），
而 `unit-attack`／`action-budget`／`targeting` 是 `Process` 声明的**流程成员**——`Emit` 会走"运行期注册"并因同名成员
已存在抛 `SignalNotRegisteredException`。成员执行面＝`OrchestrationRuntime.RunMemberAsync`。

| 成员 | 调用点（grep 可到） | 位置／顺序 | 为什么这个顺序 |
| --- | --- | --- | --- |
| `engine#action-budget` | `src/Orc.Game/Actions/Operate.cs`（`OperateEntryServices.ConsumeOperateQuotaAsync`，原 `_quotaTrigger.InvokeAsync` 之前一行） | **成员先**，旧触发器后 | 资产对它的声明是外来挂载边（无 band）；旧触发器上的注册同样是"改写本次额度"，两者互斥（同一资产只装一面），顺序无行为差异，取"新面先跑" |
| `engine#unit-attack` | `src/Orc.Foundation.Actions/AttackAction.cs`（`InvokeAsync`，原 `_trigger.InvokeAsync` 之后） | **旧触发器先**，成员后 | 该成员的**自有边为空**——攻击真实流程（校验→转发→结算）仍在旧触发器上；资产对它的声明是 `after` 包装（强磁护盾"指挥攻击后失去"），"旧触发器之后"＝"结算之后"，与声明的相位同向。反过来放会变成"攻击前失去护盾"，当场打掉本次减伤 |
| `host#targeting` | `src/Orc.Game/Cards/UnitCard.cs`（`InvokeTargetingTriggerAsync`，原 `TargetingTrigger.InvokeAsync` 之前） | **成员先**，旧触发器后 | 旧「指向触发器」的默认 handler 只声明动作缝、无业务逻辑；资产的声明是外来挂载边（默认 handler 之前），"成员先"与旧位次同向 |

**未接线的宿主**：`Hq` 没有编排实体（`ModelEntity` 只在 `CardBase` 上存在），故 HQ 的 `host#targeting` 仍只走旧触发器
——与 §21.8 第 4 条"HQ 侧维持现状"一致；`ModelMemberDispatch` 在此情形下是空操作（不会因缺实体抛错）。

**验收用例**（新增 `tests/Orc.Game.Tests/V4SampleEmissionTests.cs`，6 条，全部在 v4 面跑）：
`v4_强磁护盾_指挥攻击后失去_本次仍减伤`／`v4_磁反应装甲_被敌方指向扣层_归零移除`／
`v4_机动_首次攻击免额度消耗_并失去词条`／`v4_机动_奋战_先攻击可打三次且不能移动`／
`v4_战地救护_敌方弃置友方手牌_从卡组抽取自身`／`v4_纪奠_打出_按本局被摧毁单位数回复总部`。

**反证（不是"看着绿"）**：把 `ModelMemberDispatch` 两个入口临时短路为 `Task.CompletedTask` 再跑同一组，
结果 = **4 红 / 2 绿**：红的恰是依赖三个发射点的四条（强磁护盾／机动×2／磁反应装甲），绿的恰是只依赖
22.1 两个根因修复的两条（战地救护／纪奠）。恢复后 6/0/0。这条对照把"接线到底有没有起作用"钉死了。

### 22.4 `KeywordActionEndBridge`：**本轮未删**，以及为什么"不能删"不是保守

`§20.2` 写的是"b4 落地后整类删除"。本轮 b4 只落地了**三个**成员（`unit-attack`／`action-budget`／`host#targeting`），
而桥服务的是**另外五个**端。逐条对账（可复跑）：

```text
grep -n "EngineProcesses\.(CombatDamage|EffectDamage|BattleDamage|DamageApply|AttackDamage)" \
     src/Orc.Game/Cards/KeywordActionEndBridge.cs      → 5 条（57/59/61/63/65 行）
grep -n "\.InvokeAsync(_engine" src/Orc.Foundation.Actions/DamageAction.cs
     → 118（_apply：伤害处理端）／168（三个触发端之一）—— 仍是**直接调用旧触发器**
资产侧对这些端的声明（6 个组件）：
     diy.adjacent-cover.mitigation / keyword.magnetic-reaction / keyword.magnetic-shield /
     keyword.sponge-armor / keyword.ambush / keyword.armor / keyword.immune
```

**为什么不能照 `unit-attack` 的办法给它们加发射**（这是本节唯一的技术判断）：桥的本质不是"转发一次调用"，
而是**把同一个 `Context` 交给组件成员**（`component.InvokeAsync(memberName, ctx, ct)`），因此组件对
`view.Amount`／`view.IsZeroed` 的写回落在**伤害动作端的那份数据面**上。而 `ModelMemberDispatch` 走
`RunMemberAsync`，按 `Context` 语义**拷贝**数据面 ⇒ 写回丢失（见 `ModelMemberDispatch` 类注释里的
"数据面是副本"一段）。减伤/免疫/封顶全部依赖这条写回，所以：
- 用发射替换桥 ⇒ 数值改写静默失效（比现在更糟）；
- 发射＋保留桥 ⇒ 每个组件 handler 跑两遍（`amount-1` 会减两次）。

⇒ 删桥的硬前置是**这五个端也把"自有边"搬进成员**（同一个 `Context` 贯通整条链），也就是 b4 剩下的
`Trigger<TView>` 拆解本体。本轮按父 agent 的"停在 B"口径**不动它**，并在 §22.6 给出精确起手点。

> 另注：`engine#unit-attack` 的接线**没有**在桥上（桥的 switch 里从来没有 `UnitAttack` 分支）——
> 这正是 §21.3 第 1 条"该成员只有声明、无发射点"的成因，本轮补的也正是这一处。


## 二十三、b4 收尾：五个伤害端搬进成员、`KeywordActionEndBridge` 删除（承接 §22.4 的技术判断）

> **一句话结论**：先把 §22.4 指出的那条能力补上——**成员执行面沿用调用方的 `Context`（不复制数据面）**——
> 再把五个伤害端（`combat-damage`／`effect-damage`／`battle-damage`／`damage-apply`／`attack-damage`）的执行面
> 搬进引擎成员，最后删除桥。桥删掉后组件资产的 `wraps` 不再经"旧触发器转发"，而是**编排面自己承载**。
> 全量 **2626 / 0 / 0**（`artifacts/test-b7-node1`，8 个程序集：474+39+13+118+112+20+372+1478，
> 与 b4 基线同数——**本轮零删测**）；`KeywordActionEndBridge` 已删（src 内只剩两处历史注释）。
> 切 v4-only 与删 `Effect`／`Trigger` 类型**本轮未做**，缺口与起手点见 §23.8。

### 23.1 起点与判断（为什么第一步必须是"Context 贯通"，不是"加发射"）

§22.4 已把根因写死：桥的全部作用是**把同一个 `Context` 交给组件成员**，于是 `view.Amount`／`view.IsZeroed`
的写回落在动作端那份数据面上；而 `ModelMemberDispatch` 走 `RunMemberAsync` 按 `Context` 语义**拷贝**数据面
⇒ 写回丢失。所以替代方案的两条路都不通（§22.4 逐条）：发射⇒数值改写静默失效；发射+留桥⇒每个 handler 跑两遍。

本轮的顺序据此是：**①补贯通入口 → ②五端搬进成员 → ③删桥**。三者不可分（②做完桥才有意义地消失，
③不做则②之后的组件 handler 会被旧触发器再跑一遍）。

### 23.2 第一步：`Context` 贯通入口（三处，均为加性面）

| # | 落点 | 加了什么 | 为什么必须在这一层 |
| --- | --- | --- | --- |
| 1 | `OrchestrationRuntime.RunMemberAsync(entity, member, Context ctx, LogicEngine?, ct)` | 沿用调用方上下文执行成员；上下文**没有执行帧**时补建一个（父帧＝当前会话，与 `SignalBridge.EmitAsync` 同口径） | 与既有 `RunMemberAsync(entity, member, IDictionary?, ct)`（拷贝数据面）**并列**，既有语义零改动。补帧是 §22.1 根因 2 的同型缺陷：缺帧 ⇒ `ctx.Engine` 恒 null ⇒ 依赖引擎的组件 handler 静默早退 |
| 2 | `OrchestrationEntity.AttachOwnedEdge(memberName, OwnedEdge)` ＋ 实体级覆盖层 | 运行期给**已声明成员**追加自有边（声明边先、接入边后） | 动作实现的默认流程是 **C# 闭包**（要本局的 `IDamageActionServices`／攻击结算依赖），写不进跨对局共享的静态 `PrefabType`；但它必须落在成员自己的位次上（`OwnEdgePosition.Last`），否则"包装整个成员"没有着力点。覆盖层**每局一份**，静态声明不被任何一局写入 |
| 3 | `Trigger<TView>.InvokeAsync(engine, Context ctx, TriggerEdgeScope scope, ct)` | 沿用调用方上下文执行旧触发器，并按**位次段**取舍（Mounted／Owned／MountedAfter） | 五个端的外来改写（旧 `injects`）仍注册在旧触发器上（样例包 `effects/` 还在用），它们必须与成员**共享同一份数据面**；而默认流程已搬进成员，所以旧触发器的 `Owned` 段在成员路径上**必须跳过**，否则双重扣减 |

> 段取舍枚举 `TriggerEdgeScope` 是**过渡语义**：段之间恒按 `TriggerEdgePosition` 的顺序执行，它只做取舍、
> 不引入新顺序。它随 `Trigger<TView>` 一起退役（§23.8 第 2 步）。

### 23.3 第二步：五个端搬进成员（每端自有边＝三段）

统一形态（`DamageAction.AttachMemberEnd`／`AttackAction` 构造内）：

```text
成员自有边（OwnEdgePosition.Last ⇒ 外来挂载边/包装先跑）：
  ① 外来改写段   legacy.InvokeAsync(engine, ctx, TriggerEdgeScope.Mounted)
  ② 默认流程     RunWithLegacyViewAsync(body, ctx, ct)      ← 该端原来的自有边正文（同一个方法）
  ③ 外来尾段     legacy.InvokeAsync(engine, ctx, TriggerEdgeScope.MountedAfter)
```

| 端 | 调用点（改前 → 改后） | 默认流程正文 | 观测证据 |
| --- | --- | --- | --- |
| `combat-damage` | `DamageAction.RunAsync` 的 `trigger.InvokeAsync` → `ModelMemberDispatch.InvokeEngineMemberAsync(…, new Context(data), ct)` | `ForwardAsync` | `V4SampleEmissionTests`／`Keyword*` 全绿 |
| `effect-damage` | 同上（`EffectAsync` 入口） | `ForwardAsync` | `DiyExample09`（旧注入面）× `v4_战地救护` |
| `battle-damage` | 同上（`BattleAsync` 入口） | `ForwardAsync` | `DamagePipelineTests`（三端改写全部被处理端读到） |
| `damage-apply` | `DamageAction.ForwardAsync` 的 `_apply.InvokeAsync` → 成员调用 | `HandleApplyAsync` | 重甲／免疫／强磁护盾／海绵装甲（新旧两面） |
| `attack-damage` | `AttackAction.SettleAsync` 的 `_handle.InvokeAsync` → 成员调用 | `ApplyAsync`（默认攻击结算） | 伏击改写（`keyword.ambush` 的 `wraps: engine#attack-damage`）＋`P3ActionFamily`／`G14` |

**两处必须一起说清的判断**：

1. **旧触发器的自有段保留**（`RegisterOwned` 一行未动）。理由不是保守：`AttackDamageTrigger` 与
   `DamagePipeline.Apply/CombatDamage` 的**实例**仍被测试与诊断直接调用（`P3ActionFamilyTests`／`TriggerCardTests`／
   `G14HistoryCountTests`／`CommandCombatTests` 都在直接 `InvokeAsync` 这两个触发器并等结算），它们的语义是
   "触发器被调用＝该端完整跑一遍"。删掉自有段会让这些路径静默变成空操作；保留则两条路径各自完整、
   **生产路径只走成员**（旧触发器的 `Owned` 段在成员路径上被 `TriggerEdgeScope` 跳过），不存在双跑。
   这条重复注册随 `Trigger` 类型一并消失（§23.8 第 2 步）。
2. **`attack-damage` 的成员视图此前是错的**（`EngineProcesses.Build` 用了触发端的 `damageEvent` 形状
   `Source/Target/Amount/Kind`）。成员一旦真的被执行，挂在它上面的伏击组件读 `view.Attacker` 就会撞上
   不存在的字段名（承载视图的字段名字空间＝**目标成员**的视图，不是包装自己声明的视图）。
   本轮按旧视图 `AttackDamageTriggerView` 修正为 `Attacker/Target/Resolution/TriggerCard/Result`。
   这是 §21.3 那张表里没有暴露的隐藏缺陷：成员不被执行时，声明错也不会有人发现。

### 23.4 第三步：删除 `KeywordActionEndBridge` 及其三条接缝

| 删除项 | 位置 | 为什么现在可以删 |
| --- | --- | --- |
| `KeywordActionEndBridge` 整类 | `src/Orc.Game/Cards/KeywordActionEndBridge.cs`（已删） | 它的唯一职责＝"把同一个 `Context` 交给组件成员"。五个端现在由成员直接执行，组件 `wraps` 由 `OrchestrationRuntime.RegisterWraps` 承载，同一条能力不再需要转发 |
| 装配期接线 | `CommandManager` 构造内的 `model.Assembled += … KeywordActionEndBridge.Attach(…)` | 同上；`Assembled` 事件保留（只剩"装配完成"的观测语义，注释已改） |
| 逐实例撤销 | 桥登记的 `ComponentAttachment.OnDispose(detach)` | 包装登记的句柄由 `RegisterWraps` → `entity.Track(handle)` 持有，随组件实体销毁精确撤销（既有路径，零改动） |

### 23.5 测试改动（0 删除、0 跳过；1 条断言改写）

| 文件 | 改动 | 理由 |
| --- | --- | --- |
| `tests/Orc.Game.Tests/EdgePositionMigrationTests.cs`（`重甲与免疫_改写位次为外来边且在自有边之前`） | 断言"`重甲减伤`／`免疫归零` 出现在**触发器事件表**的 `Mounted` 位次" → 断言"包装登记表里有 `member:engine#combat-damage`／`member:engine#damage-apply` 的 `before` 项，且 handler id 逐条相同" | 该断言原来记录的正是**桥**的存在形态（组件包装被转发到旧触发器）。迁移后包装由编排面承载，同一语义的正确读面是 `Wraps.All`。**自有边两条断言（`转发到处理端`／`扣除数值` 的 `Owned` 位次）逐字未改**，行为断言（免疫归零使实扣为 0）也逐字未改 ⇒ 判据没有放宽，只是换了读面 |

其余测试**未改**：直接调用旧触发器的用例（`P3ActionFamilyTests`／`TriggerCardTests`／`G14HistoryCountTests`／
`CommandCombatTests`／`DamagePipelineTests`／`KeywordEffectBatch2Tests`）全部原样通过——这正是 §23.3 第 1 条
"旧触发器自有段保留"要保住的东西。

### 23.6 全量阶梯（每层一次全量，均带 `--artifacts-path`；常驻 `Orc.Manager` 未杀）

| 层 | 命令 | 结果 |
| --- | --- | --- |
| 子集①（v4 面行为） | `--filter FullyQualifiedName~V4SampleEmission` | 6 / 0 / 0 |
| 子集②（伤害面） | `--filter FullyQualifiedName~Damage` | 58 / 0 / 0 |
| 子集③（词条面） | `--filter FullyQualifiedName~Keyword` | 222 / 0 / 0 |
| 子集④（位次／攻击面） | `--filter ~EdgePositionMigration|~P3ActionFamily|~G14|~TriggerCard` | 31 / 0 / 0（改写后） |
| 子集⑤（样例包／装载链） | `--filter ~Diy|~SpaceWar|~CardEffectLoader` | 40 / 0 / 0 |
| **节点（迁完五端＋删桥）** | `dotnet test OrcEngine.sln --artifacts-path artifacts/test-b7-node1` | **2626 / 0 / 0**（8 程序集：474／39／13／118／112／20／372／1478；`Orc.Manager` 未杀，本轮未出现已知的进程级 3 红） |

### 23.7 完成判据实测（四条 grep ＋ 桥）

```text
grep -rn "new Trigger<" src/ --include=*.cs        → 38   （未动：删 Trigger 属下一步）
grep -rn "class Effect\b" src/ --include=*.cs      → 1    （未动）
grep -rnw "\bBus\b" src/ --include=*.cs            → 0    ✓
grep -rn "PrefabJson" src/ --include=*.cs          → 23   （未动）
grep -rn "KeywordActionEndBridge" src/ --include=*.cs → 2（**仅两处历史注释**：ModelMemberDispatch／DamageAction；实现已删）
```

### 23.8 剩余缺口与起手点（按顺序）

1. **切 v4-only**（§21.3 七条已在 §22 全部落地，本轮又补齐了伤害端的执行面）：
   `samples/*/effects/` → `samples/_legacy/`，manifest 去 effects sidecars，夹具改读 `effects-v4/`。
   起手点：`DiyExampleKit.TryLocatePack`／`SpaceWarCardPackTests` 的 `effects/` 路径，以及
   `CardDistributionValidator`（只认 `*.prefab.json`，要改认 `*.component.json`，§21.7 第 3 项）。
2. **删 `Trigger` 家族**：本轮之后旧触发器上只剩三类东西——①自有段（§23.3 第 1 条的兼容副本）②外来注入（样例包
   旧效果）③测试的直接调用。①随 v4-only 一起消失（无旧效果后，"直接调用触发器"的语义只剩测试）；
   ②需要把 `CardEffectLoader.ApplyFrameworkInjects` 改成"挂到成员边"（`MountAtRuntime` ＋ 端点实体，
   `SignalBridge.Mount` 是现成样板），这是删 `Effect` 的最后一块。
3. **`KeywordLoadContext.AttackDamageTrigger`／`DamageAction` 两个字段已成死代码**（桥是唯一消费者）。
   删除时连带 `CommandManager` 的构造实参（4 处），属小改动，放在删 `Trigger` 那一批更安全。

## 二十四、v4-only 切换（§23.8 第 1 项，已完成）

> **一句话结论**：生产装载与卡装配路径已**只认 v4**——5 个样例包的 `effects/` 归档到 `samples/_legacy/`，
> 5 份 manifest／lock 按内容指纹重算，`CardBase` 的旧效果链降级为**唯一残口**（卡数据面没有 effects 声明时）。
> 由此 `CardDistributionValidator`／`CardLoaderTrigger.EffectExists` 两处"效果资产存在性"判据补上了 v4 分支——
> 它们此前只认 `*.prefab.json`，是 v4-only 后的假阳性来源。

### 24.1 判定口径（为什么不是"删代码"而是"换装载面"）

v4-only 不是删旧类型，而是**让旧格式不再进入生产装载**：
卡数据里的 `effects.prefabs` 写的 id 与新组件 id **完全一致**（如 `diy.volley.effect`），
所以卡数据零改动，只是解析目标从 `effects/*.prefab.json` 换成了 `effects-v4/*.component.json`。
这也解释了为什么本项能在不动任何 `Effect`／`Trigger` 类型的前提下完成。

### 24.2 src 三处改动

| 文件 | 改动 | 判据 |
| --- | --- | --- |
| `src/Orc.Game/Cards/CardBase.cs` | `LoadModelComponents()` 返回 `(Declared, Mounted)`；`LoadEffectsAsync` 仅在 `Mounted == 0 \|\| Mounted < Declared` 时执行 | 数据面声明了 effects 且全部在编排目录解析到 ⇒ 纯 v4；否则（无声明＝代码注册面路径／有装配失败）走兜底 |
| `src/Orc.Packages/Loading/CardLoaderTrigger.cs` | `EffectExists` 增加 v4 判据：`effects-v4/<localId>.component.json` 存在即可解析 | 组件资产**没有 sidecar** ⇒ 不进 `AssetRegistry`，只查注册面会把每张声明了效果的卡判成"引用不存在" |
| `src/Orc.Game/Cards/Data/CardDistributionValidator.cs` | 新增 ③b 段扫 `*.component.json` 取 `asset.id`；并入 ⑧ 的解析集合；`SuccessfulLibrarySnapshotCount` ＝ 旧快照数 ＋ v4 组件数 | §21.7 第 3 项（预检只认旧格式）——否则 v4-only 后预检门对每个包都报假阳性 |

### 24.3 资产：归档 + 身份重算

1. **归档**：`samples/card-pack-{across-the-pacific,bulwark,demo,diy,space-war}/effects/` →
   `samples/_legacy/<pack>/effects/`（20 份旧资产 ＋ 20 份 sidecar 原样保留；`_legacy` 不含 manifest，
   因此不被 `PackageScanner`／`AssetMigrationTool.EnumeratePackageRoots` 当作包）。
2. **manifest**：`metadata[]` 去掉 `effects/*.meta.json` 项；`version` 按**内容指纹**重算。
   指纹算法（`PackageVersion.ComputeContent`）＝ `SHA-256(packageId + Σ[段字节长 + 相对路径 + 规范化 sidecar 文本])`
   取前 16 字节——本轮用脚本精确复现，并以测试期望值逐位校验通过（`space-war`／`atp` 两包的期望值均命中）。
3. **lock**：`lock.formal.json` 的 effect 条目随 sidecar 消失而移除（5 个包各重算）。
4. **落盘方式**：manifest／lock 由 `tools/Orc.Tools.Migrate --apply` **规范重写**（不是手写 JSON）——
   因为 `AssetMigrationTool.Plan` 会把自己生成的 manifest 文本与现有文本逐字节比较，
   任何格式差异都会让 `样例包_每个源文件都有sidecar且清单为v2与两lock一致` 判红。

### 24.4 测试改写清单（8 个文件；0 删除、0 跳过）

| 文件 | 原断言 | 新断言 | 理由 |
| --- | --- | --- | --- |
| `BulwarkCardPackTests` | 旧 prefab 的 `injects`／`judicatorWrappers`；`EffectsDirectory` 存在 | v4 的 `mounts`／`judge-wraps`；`ComponentsDirectory` 存在且 `EffectsDirectory == null` | 观测面从旧格式换到 v4；被测意图（机动挂额度成员、固守两条判定器包装）不变 |
| `SpaceWarCardPackTests` | 同上 ＋ `priority` 10/20/30 分层 | 三个 v4 `wraps`（`engine#damage-apply` before）＋ 强磁护盾 `engine#unit-attack` after ＋ 磁反应 `host#targeting` 挂载 | **priority 分层在新模型无对应物**（R1i：band／priority 数值退役，同相位次序由声明序推出）——这一条属"旧机制存在性断言"，改写为结构断言并在测试里写明 |
| `AssetEditorTests` | 依赖样例包自带 `effects/demo.rally.prefab.json` | 构造器从 `_legacy` 复制该文件 ＋ sidecar 进临时包 | 编辑器写入面**仍是旧格式**（门 B 未做）；被测的是"编辑器对旧格式资产的行为"，故只补前置数据、不改被测语义 |
| `DiyExampleKit` | `TryLocatePack` 查 `effects/`；`Prefabs.LoadDirectory` 装旧库 | 指向 `effects-v4/`；`AssetPackLoader.Load` 装组件资产 | 夹具跟随生产装载面 |
| `CardPackDemoTests` | 同上 ＋ `Prefabs.TryGetPrefab(demo.rally)` | `Model.Catalog.Contains(demo.rally)` | v4 组件进编排目录，不进旧预制体库 |
| `BulwarkKeywordTests` | 手工 `PrefabJson.Deserialize` 装 3 个旧 prefab；固守诊断段读旧 `judicatorWrappers` | `AssetPackLoader.Load`；诊断段读 v4 `JudgeWraps` | 同上 |
| `SpaceWarKeywordTests` | 手工装 3 个旧 prefab | `AssetPackLoader.Load` | 同上 |
| `AssetSchemaMigrationTests` | `PackDirectories()` 扫 `samples/card-pack-*`；落盘测试读 `samples/card-pack-space-war` | 扫 `samples/_legacy/card-pack-*`；落盘测试复制到**临时包**再 `MigratePackToDisk` | 迁移器的输入本就是"旧格式"，v4-only 后旧格式只存在于归档；落盘不能写归档（只读历史） |

### 24.5 验证（分项目，全量留待测试改造后）

| 项目 | 结果 |
| --- | --- |
| `Orc.Packages.Tests` | **372 / 0 / 0** |
| `Orc.Tests` | **474 / 0 / 0** |
| `Orc.Script.Tests` | **39 / 0 / 0** |
| `Orc.Ai.Tests` | **13 / 0 / 0** |
| `Orc.Mcp.Tests` | **118 / 0 / 0** |
| `Orc.Server.Tests` | **112 / 0 / 0** |
| `Orc.Manager.Tests` | **20 / 0 / 0** |
| `Orc.Game.Tests`（相关子集：Keyword／Demo／Migration／V4／SpaceWar／Bulwark／Diy／CardDistributionValidator／CardModelBridge） | **298 / 0 / 0** |

**踩过的坑（值得记住）**：`dotnet test --no-build` 用的是**该项目自己 bin 里的旧 Orc.Game.dll**——
改了 `src/Orc.Game` 后只 build `Orc.Game.Tests` 不会刷新 `Orc.Mcp.Tests` 的输出，
于是 `SandboxPrimitiveChainTests` 三条会以旧二进制报"效果不生效"的假红。改共享库后必须重建目标测试项目。

### 24.6 未做：删 `Effect`／`Trigger` 的硬前置（本轮实测确认）

本轮把"旧格式"从装载面清掉后，`Effect` 家族的**活体子类只剩 5 个、且全在框架内部**：

```
src/Orc/Cards/Effect.cs            PassiveEffect / ActiveEffect<TView>（抽象基类）
src/Orc/Cards/DynamicEffect.cs     DynamicPassiveEffect / DynamicActiveEffect（由 EffectSnapshot 构造）
src/Orc.Game/Cards/DeathrattleSystem.cs  DeathrattleEffect（抽象；src 内无具体子类，仅测试探针）
```

业务侧 C# 效果子类**已清零**（7 个词条已由 v4 组件承载）。剩下的消费者只有两条链：

1. **卡装载兜底**（`CardBase` → `CardEffectLoader`，864 行）——本轮保留，只在"卡数据面无 effects 声明"时执行。
2. **`DynamicEffectFactory` 的 4 个生产调用点**：`KeywordManager:440`（词条 `assemblyKey` 数据壳）、
   `DeclarativeKeywordComponent:12`、`CardEffectLoader:267`、`EffectRuntime.DynamicEffects:370`。

**真正的墙是卡面文本编译器**：`EffectCompiler.Compile` 仍输出旧 `EffectSnapshot`
（`mainTrigger` ＋ `otherTriggers` ＋ `wrappers` ＋ `injects`），而 v4 要的是"成员 ＋ 挂载 ＋ 包装"。
只要它还产旧结构，那 30＋ 条 `EffectRuntimeEndToEndTests`／`RandomBattleKeywordChainTests`／
`KeywordEffectChannelTests` 就必须靠兜底链跑。

**下一批的正解（按代价从低到高）**：

| 路径 | 做法 | 代价 |
| --- | --- | --- |
| **A（推荐）** | 写 `EffectSnapshot → ComponentAsset` 转换器，**复用 `AssetSchemaMigrator`**（它已有 `LegacyViewCatalog.Resolve(viewType)` 这套 viewType→字段清单映射）；`CardEffectLoader` 的两条装载路径改成"转换后注册进编排目录" | 一个转换器 ＋ 测试入口改造；无需动模板模型 |
| B | 让 `EffectCompiler` 直接产 v4（改 `EffectTemplate` 的领域模型：触发器/事件 → 成员/边） | 模板系统重写，牵动全部 DSL 模板与快照比对测试 |
| C | 保留兜底链不清（放弃删 `Effect`） | 与 R8"全量替换"冲突 |

选 A 之后，`Trigger<TView>` 才可能删——顺序**不能反**（`Effect` 持有 `Trigger` 实例，
`Effect.cs` 内 14 处、`DynamicEffect.cs` 内 28 处引用）。

### 24.7 顺带记下的两处小账

1. `samples/_legacy/` 是**新增未跟踪目录**（`git status` 显示 `?? samples/_legacy/`）——提交时不要漏，
   否则迁移器与 `AssetSchemaMigrationTests` 的输入会缺。
2. `KeywordLoadContext.AttackDamageTrigger`／`DamageAction` 两个字段仍是死代码（§23.8 第 3 项），
   随 `Trigger` 批次一起清。

## 二十五、旧快照 → v4 组件（路径 A 落地，删 `Effect` 的第一块拼图）

> **一句话结论**：卡装载面的效果装配已能**把旧 `EffectSnapshot` 当场转成 v4 组件**并走组件实体执行——
> csx 效果从此不再需要 `Effect` 实例；只有三类"旧来源"仍回退旧链（`assemblyKey`、`dynamic` 下编译不兼容的 csx、
> 反制卡宿主）。这使 `Effect` 的存活面从"整个卡装载面"缩到"三条明确的旧来源支线"。

### 25.1 新增：`LegacySnapshotComponentBinder`（`src/Orc.Game/Cards/`）

转换链**零新序列化／零新映射表**，三块都是现成的：

```
EffectSnapshot
  ├─ PrefabJson.SerializeForDisk(snapshot)         → 旧格式 JSON 文本（既有落盘契约出口）
  ├─ AssetSchemaMigrator.Migrate(json, id)         → v4 组件 JSON（viewType→字段清单复用 LegacyViewCatalog）
  └─ AssetSchema.Read(migrated.Json)               → ComponentAsset
       → HandlerRegistry.RegisterScript（装载期预编译 csx）
       → HandlerRegistry.RegisterJudge（判定器包装）
       → Catalog.Add(asset.ToPrefabDefinition(...))
```

结果按「序列化文本的 SHA-256 前 16 字节」缓存 `ComponentAsset`（不可变、跨运行时安全）——
迁移器是文本处理，而每局都会对同一批快照重复触发；缓存后 `handler 注册 + 预制体展开` 仍按运行时各做一次。

### 25.2 `CardEffectLoader` 的装配段改成三分类

| 分类 | 判据 | 处理 |
| --- | --- | --- |
| **v4** | 有编排实体 ＋ 非反制宿主 ＋ 快照无 assemblyKey ＋ csx 能在承载视图下编译 | 转换 → `AddComponent`（组件实体执行） |
| **旧链（回退）** | `assemblyKey` 成员 / csx 编译不兼容 / 反制宿主 / factory 注册 | `DynamicEffectFactory` ＋ `card.AddEffect`（行为不变） |
| **隔离** | 快照缺失、转换抛其它异常、挂载失败 | 写 `Error` 日志、跳过该条、不阻断加载 |

回退用**专用异常**表达（`LegacyAssemblyKeyNotSupportedException` / `LegacyScriptUnsupportedException`），
不是"catch 一切再猜"——两类"旧来源"各自有明确判据，日志也指向具体原因。

### 25.3 三个根因（都是"观测面变了、行为没变"之外的真问题）

1. **视图短名解析不了**：`EffectSnapshot.ViewType` 存的是 `Type.Name`（`CardEventView`），而旧 prefab JSON 存全名
   （`Orc.Cards.CardEventView`）。→ `LegacyViewCatalog.FindType` 补短名兜底，**要求简单名唯一**（多义＝拒绝，仍不猜）。
2. **band 只认样本实态**：迁移器原来硬要求「该显示名的期望 band」与常量表一致，导致文本编译器模板
   （`attack_basic` 用 `单位攻击触发器 + Initiate`）整类被拒。→ 改成**按语义映射**：
   `Rewrite`/`Veto`→before、`Commit`→after、其余已知段（`Initiate`/`Announce`/`CounterGuard`/`Counter`/`Adjust`/`Apply`）
   与无 band → 外来挂载边；未知 band 仍拒绝。
3. **`dynamic` 破坏明确赋值**：`if (view.Unit is not Card deployed || ...)` 在承载视图下报
   "使用了未赋值的局部变量 deployed"（强类型视图下合法）。→ 这类 csx 判为"编译不兼容"**回退旧链**，
   而不是隔离（隔离会让这类效果整类静默失效）。这是第三次撞同一形态（前两次是 4 份样例资产与工具链测试 csx，
   手工拆 `if` 解决），本轮把它固化成可回退的判据。

### 25.4 验证（分项目，全量仍留待测试改造后）

| 范围 | 结果 |
| --- | --- |
| `Orc.Game.Tests` 效果/词条/光环/卡服务/DIY/随机/工具链/预制体 相关子集 | **668 / 0 / 0** |
| `Orc.Packages.Tests` | **372 / 0 / 0** |
| `Orc.Mcp.Tests` | **118 / 0 / 0** |
| `Orc.Tests` | **474 / 0 / 0** |

**0 删除、0 跳过**。改写的测试只有一处：`CardToolchainEndToEndTests` 的 `DeploySetCsx` 拆成两条 `if`
（理由同 25.3 第 3 条）。

### 25.5 剩余：`Effect` 仍不能删的四条链（按处置难度排序）

| # | 链 | 现状 | 处置 |
| --- | --- | --- | --- |
| 1 | **反制卡宿主**（`AssembleCounterEffectHandlers`） | 效果折叠成 `CounterCard` 的 Counter 段 handler 集，依赖 `card.Effects` 列表 | 需要"组件成员挂到 Counter 段"的等价机制（一批） |
| 2 | **`assemblyKey` 成员** | C# 委托来源，v4 只收 csx | 二选一：①写适配器把委托包成 `KernelHandler`；②把这些效果改写成 csx（长期正解） |
| 3 | **`Declarations`（factory 注册）** | `registry.Register(id, factory)`，仅 1 处测试（`CounterEffectAssemblyTests:602`） | 改成 csx 或随反制卡一并处置 |
| 4 | **`EffectRuntime.DynamicEffects`** ＋ `KeywordManager` 的 assemblyKey 数据壳 | 运行期动态效果面 / 词条回退分支（内建词条已被 v4 全覆盖，分支是死路） | 词条回退可删；运行期动态效果面需产品决策 |

`Trigger<TView>` 仍不能动：`Effect.cs`（14 处）、`DynamicEffect.cs`（28 处）持其实例，
顺序仍是 **先 `Effect` 后 `Trigger`**。

### 25.6 一处顺带确认的事实

`CardBase.LoadModelComponents()` 只处理 `effects.prefabs`，**不含 `effects.inline`**——
内联效果一直靠"代码注册面（`DeclarePrefab`）＋ 装载链"承载。本轮 v4 路覆盖的正是这条链，
所以内联效果现在也走组件实体；而 `prefabs` 声明的那批走 `LoadModelComponents`。两条路互不重叠、不会双装
（`Orc.Game.Tests` 的 668 条子集含大量 DIY 内联用例，可作证据）。

## 二十六、删 `Effect`／旧装载链（本轮：A/B/C/D）

> **一句话**：`Effect` 家族（`Effect`／`PassiveEffect`／`ActiveEffect`／`DynamicEffect*`）、卡上效果容器、
> 旧装载链（`CardEffectLoader`）与旧装载计划（`EffectLoadPlan`）已从 `src/` 删除；
> 卡装载面的效果装配收敛为**唯一一条** v4 路径（目录直引 ＋ 旧快照现转组件）。

### 26.1 删除清单（src）

| 文件 | 处置 |
| --- | --- |
| `src/Orc/Cards/Effect.cs` | 删（`Effect`／`PassiveEffect`／`ActiveEffect<TView>`／`ISerializableEffect`／`EffectInjection`／`DisposalState`／`DisposalSource`） |
| `src/Orc/Cards/DynamicEffect.cs` | 删（`DynamicEffectFactory`／`DynamicPassiveEffect`／`DynamicActiveEffect`／`Builder`／`TriggerReflection`） |
| `src/Orc/Cards/Prefabs.cs` | → `EffectSnapshot.cs`，只留 DTO；`EffectLoadPlan`／`EffectLoadStep`／`EffectLoadStepKind` 删 |
| `src/Orc/Cards/PrefabManager.cs` | → `SnapshotStore.cs`（只留 `RegisterPrefab`／`TryGetPrefab`／`RegisterHandler`／`TryGetHandler`／`PrefabIds`／`HandlerKeys`） |
| `src/Orc.Game/Cards/CardEffectLoader.cs` | → `CardEffectRegistry.cs`（只留 `DeclarePrefab`／`GetPrefabDeclarations`／`CardEffectLoadContext.PrefabDeclarations`） |
| `src/Orc.Game/Effects/EffectRuntime.DynamicEffects.cs` | 删（`EffectRuntime` 主体保留：targeter／判定器／随机服务） |

### 26.2 卡装配的新形态（`CardBase.LoadModelComponents`）

```
① effects.prefabs 里的 id ──→ runtime.Catalog 直取 ──→ AddComponent
② CardEffectRegistry.DeclarePrefab 声明的 id
      ├─ 目录已含 ──────────────────────────────→ AddComponent
      └─ 目录无 → SnapshotStore.TryGetPrefab → LegacySnapshotComponentBinder.Register → AddComponent
                  └─ 旧来源（assemblyKey／csx 编译不兼容）→ Error 日志 + 跳过（无回退）
```

同一 prefab id 只装一次（`mountedIds` 去重）；单条失败＝隔离留痕，不阻断卡加载。
`CardBase.LoadEffectsAsync` 与对 `CardEffectLoader` 的调用已删除。

### 26.3 同步删除的旧面

- `Card`：效果容器（`Effects`／`AddEffect`／`RemoveEffect`／`RemoveEffectSilently`／`MountPassiveEffects`／`ContainsEffect`／`Effect*Core`）全删——卡只剩数据组件。
- `CardLifecycle`：`CardLoadout`（挂载/销毁链）删；`CardLoadoutProcessor` 只留「放置初始化」与「card.destroyed → 卡牌登记移除」。
- `LogicEngine`：`RegisterCardMountCompletedAction`／`RunCardMountCompletedActions`（效果装载管线扩展点）删；`Prefabs` 属性类型改 `SnapshotStore`。
- 审查面：`Orchestration` 的"效果节点采样"、`Trigger` 的宿主注入与注册者归因、`SignalBridge`／`AuditGraphBuilder`／`JsonValueWriter` 的 `Effect` 分支删。
- `PrefabJson`：只留 `SerializeForDisk`（`LegacySnapshotComponentBinder` 的唯一入口）。

### 26.4 编译状态

- `src/Orc`：**0 错误**（`--artifacts-path D:/OrC-KSD/artifacts/test --no-restore`）。其余项目进行中。
- 环境注记：`artifacts/test` 下的构建产物被外部进程以「拒绝写入」方式占用（`MSB3491`／`CS2012`），
  只能"先重命名再构建"；`artifacts/build-src.sh` 封装了这个重试（不是代码问题）。

### 26.5 最终编译状态（本轮判据实测）

`dotnet build OrcEngine.sln --artifacts-path "D:/OrC-KSD/artifacts/test" --no-restore`：

| 范围 | 结果 |
| --- | --- |
| `src/` 15 个项目逐个构建 | **全部 exit=0、0 错误**（Orc／Orc.Ai／Orc.Client／Orc.Foundation.Actions／Orc.Game／Orc.Manager／Orc.Mcp／Orc.Mcp.Bridge／Orc.Packages／Orc.Plugins.BuiltIn／Orc.Protocol／Orc.Script／Orc.Server／Orc.UI／Orc.UI.Contracts） |
| `samples/` 4 个项目 | 全部 exit=0 |
| `tests/` 3 个项目 | **59 条错误**（Orc.Game.Tests 43／Orc.Tests 14／Orc.Packages.Tests 2）——本轮允许，下一轮处理 |

三条完成判据实测：

| 判据 | 实测 | 结论 |
| --- | --- | --- |
| `grep -rn "new Trigger<" src/` | **32 处真实实例化（＋4 处注释）** | ✗ **未达成**（见 26.6：`Trigger<TView>` 是新模型执行载体，非废弃代码） |
| `grep -rn "class Effect\b" src/` | **0** | ✓ |
| `grep -rnw "PrefabManager" src/` | **1（仅 `SnapshotStore` 里"原 `PrefabManager`"的历史注记）** | ✓ |

### 26.6 `Trigger<TView>` 为什么没删（按硬约束 3 保留并注明）

保留理由（已写入 `src/Orc/Core/Trigger.cs` 文件头）：它已不是旧效果链，而是新模型的**执行载体**——

- `CardMemberTable.Trigger<TView>` 按类型预制体声明**物化成员句柄**（卡／单位／指令／反制／HQ 全走它）；
- `I*Action` 契约（`IAttackAction.Trigger/Handle`）与 `ActionTriggerEnd` 仍以它为两端身份，`ActionEnds.TriggerOf` 是唯一按地址反查实例的桥；
- 对局级触发器（`CommandManager` 11 个、`DamagePipeline` 4 端、`OrderFlow` 2 个、`SandboxPrimitives` 7 个、
  `CardLifecycle` 2 个、`RetriggerSystem` 1 个、5 个动作类各自的端）都是它的实例。

删它＝重写整个动作分发层（U5），不是"删废弃代码"。本轮**已**把随旧链存在的部分删掉：
`TriggerEdgeScope`（三段兼容桥的唯一消费者）、`Effect` 家族、旧装载链、`EffectLoadPlan` 系列。
`TriggerEdgePosition` 仍被 `Trigger<TView>` 的注册位次与审查面（`AuditChain*` 的 `position`）使用，一并保留。

**下一轮（U5）的清单**：①`ActionTriggerEnd.Trigger` 字段删除 → `ActionEnds.TriggerOf` 与 4 处调用点改地址；
②`I*Action` 的 `Trigger/Handle` 属性删除；③`CardMemberTable` 物化面改 `MemberAddress`；
④`CommandManager`／`DamagePipeline`／`OrderFlow`／`SandboxPrimitives` 的实例化点改成员调用。

### 26.7 保留项（不删）及理由

| 类型 | 理由 |
| --- | --- |
| `HookRegistry` / `Updates` | 内核信号登记面，`LogicEngine`／`KernelSignals` 在用 |
| `KeywordEffectAssembly` | `KeywordPrefabLibrary`／`KeywordComponents` 经它做词条装配（本轮只删库装载段） |
| `EffectRuntime`（主体 + `Board` partial） | targeter／判定器／随机服务；只删 `DynamicEffects` partial 与其结果类型 |
| `SnapshotStore`（原 `PrefabManager` 的注册面） | `RegisterPrefab`／`TryGetPrefab`／`RegisterHandler` 仍被卡包/词条/插件用 |
| `PrefabJson.Serialize`／`Deserialize` | **明确豁免**：卡数据 `components.effects.inline` 的读写面（内联效果仍以旧快照随卡分发）；`EffectCompiler.CompileToJson` 也用它 |
| `Trigger<TView>` 及其周边（`TriggerEvent`／`ITriggerMetadata`／`TriggerKind`／`TriggerEdgePosition`／`TriggerRegistration`／`TriggerEventInfo`／`ExecutionFrame`） | 见 26.6 |

### 26.8 行为面变更（测试轮要验的）

1. **反制卡宿主的"效果折叠为 Counter 段 handler 集"删除**：`AssembleCounterEffectHandlers` 依赖 `Effect` 列表；
   反制卡现在与普通卡一样走 v4 组件装配。
2. **亡计内容通道清空**：`DeathrattleEffect`／`GrantWithContentAsync`／`KeywordComponent.EmbeddedEffects` 删除；
   `DeathrattleRules.ExecuteAsync` 保留为恒 false 的插入点。
3. **运行期动态效果面删除**：`ProgramKeywordComponent` 的 `attach.prefab` op 改为明确 `NotSupportedException`。
4. **旧格式 `effects/` 目录不再装载**：`CardPackAssembler` 对只有旧目录的包记失败；`McpService.MatchLaunch`
   对旧效果库目录直接返回失败；`CardTools.load` 不再装载效果库（`effectsLoaded` 恒 0）。
5. **效果装载完成动作扩展点删除**：`RegisterCardMountCompletedAction`／托管清理注册（修饰器/光环按来源撤销）
   随 `Effect` 删除；v4 组件的卸载由 `OrchestrationEntity` 自己的撤销链承担。
6. **`Card.Effects` 容器删除**：`CardCastActions.Of` 对无组件实体的卡恒返回空；`KeywordServices`／`MatchActionServices`
   的"清空效果"步骤改为注释说明（由组件卸载承担）。

## 二十七、`Trigger<TView>` 家族退役（批次 7；本轮唯一目标）

> **一句话**：`Trigger<TView>`／`TriggerEvent<TView>`／`ITriggerMetadata`／`TriggerKind`／`TriggerEdgePosition`／
> `TriggerRegistration`／`TriggerWrapperRegistration`／`TriggerEventInfo`／`TriggerRegistry`／`TriggerLayer`
> 与两个"只做验证的触发器子类"全部删除。执行体改为**实体成员上的边**：
> 卡的默认链＝宿主实体的自有边，对局流程＝引擎实体的自有边，订阅／守卫＝引擎成员的外来边。
> `new Trigger<` 在 `src/` 由 **37 → 0**；`src/` 15 个项目全部 **0 错误**。

### 27.1 删除清单（src）

| 文件 | 处置 |
| --- | --- |
| `src/Orc/Core/Trigger.cs` | **删**（`Trigger<TView>` 全部实现 ＋ `TriggerKind`／`TriggerEdgePosition`／`TriggerRegistration`／`TriggerWrapperRegistration`／`TriggerEventInfo`）。`ExecutionFrame` 移出到独立文件（见 27.5） |
| `src/Orc/Core/TriggerEvent.cs` | **删**（`TriggerEvent<TView>`） |
| `src/Orc/Core/TriggerMetadata.cs` | **删**（`ITriggerMetadata`） |
| `src/Orc.Game/Triggers/TriggerRegistry.cs` | **删**（对局级登记簿＋`TriggerRegistryEntry`；端＝成员，边接入即进账，不需要第二本账） |
| `src/Orc.Game/Triggers/TriggerLayer.cs` | **删**（低层／外部分层标注） |
| `src/Orc.Game/Triggers/CostCheckTrigger.cs` | **删**（改成守卫边，见 27.3） |
| `src/Orc.Game/Triggers/CounterUseTrigger.cs` | **删**（改成守卫边，见 27.3） |
| `src/Orc.Game/Cards/MatchNamedTriggers.cs` | **删**（`injects` 按显示名解析的回退表） |
| `src/Orc.Game/Cards/NamedTriggerTable.cs` | **删**（卡侧具名触发器表） |

**新增（承接执行体）**

| 文件 | 作用 |
| --- | --- |
| `src/Orc/Core/ExecutionFrame.cs` | 执行帧（原 `Trigger.cs` 尾部类型；新模型的帧，非触发器专属） |
| `src/Orc/Core/Model/MemberEdges.cs` | `MemberEdges.For<TView>(handler)`：把强类型处理器接成一条边（视图绑定器与 csx 同源、执行完即封） |
| `src/Orc.Game/Actions/ActionEdges.cs` | 动作端 → 引擎成员自有边的接入面（缺编排运行时＝构造期明确失败） |
| `src/Orc.Game/Triggers/CardPlayGuards.cs` | 费用校验／反制使用检查的守卫边（取代两个验证触发器子类） |

### 27.2 `CardMemberTable` 的新形态

```
旧：CardLegacyTriggerFactory.Materialize(...)   // 按成员名 new Trigger<XView> ＋ registerNamed(显示名)
新：new CardMemberTable(type, source, host)     // 只回答"成员在哪个地址"
      └─ AddressOf(name) → host#<成员名>        // 未声明成员＝KeyNotFoundException（构造期 fail-fast）
```

- `Trigger<TView>(name)` 方法与 `CardLegacyTriggerFactory` 整类（含 `LegacyDisplayNames`）删除。
- 卡牌类改为持有 `MemberAddress`：`UnitCard.PrePlayTrigger/PlayTrigger/DeployTrigger/…`、
  `OrderCard.PrePlayTrigger/PlayTrigger`、`CounterCard.UseCounterTrigger`、`Hq.TargetingTrigger` 全部由
  `Trigger<XView>` 改为 `MemberAddress`（同名属性，调用点语义不变）。
- 执行改走宿主实体：`CardBase.InvokeMemberAsync(member, ctx, ct)` →
  `OrchestrationRuntime.RunMemberAsync(ModelEntity, member, ctx, engine, ct)`。
- 卡牌默认链改为**自有边**，构造期经 `CardBase.AttachMemberEdge(member, edge)` 登记；
  实体尚未创建（`LoadAsync` 之前）＝挂起，`EnsureModelEntity()` 创建后按接入序落表。

### 27.3 `CostCheckTrigger`／`CounterUseTrigger` 的替代实现（行为等价）

| 旧 | 新 |
| --- | --- |
| `CostCheckTrigger : Trigger<CardTriggerView>`，`BindValidation(CostCheck)`，挂在 `pre-play`／`play` | `CardPlayGuards.AttachCostGuard(card, member, resolver)`：该成员的**第一条自有边**——判定器同一（`JudicatorNames.CostCheck`）、同一被判定对象（卡自身 `Ref`）、同一玩家索引（`Owner?.Index`） |
| `CounterUseTrigger : Trigger<CardTriggerView>`，`BindValidation(CounterUse)`，挂在 `use-counter` | `CardPlayGuards.AttachCounterUseGuard(card, resolver)`：同一判定器（`JudicatorNames.CounterUse`）与同一主体 |
| 不合法＝`ValidationRejected`（不绑视图、不执行事件、留痕） | 不合法＝写数据面 `guard:rejected`＋`guard:reason`，然后 `ctx.Stop()`（本级剩余步骤不跑）；入口读同一份 `Context` 取回拒绝类别 |

- 入口映射保持：预打出拒绝 → `PlayFailureReason.PrePlayPointShortage`；打出段复验 → `PlayVerificationRejected`；
  反制 → `CounterNotOwnerTurn`／`CounterPointShortage`／`CounterRejected`（类别来自判定器）。
- `CanPlay` 的三处 `EvaluateValidation` → `CardPlayGuards.EvaluateCost/EvaluateCounterUse`（同一判定源，无缓存）。
- 判定器解析仍为**装配期一次**（`CardPlayGuards` 在接入边时 `Resolve`）——未注册名＝构造期 fail-fast。

### 27.4 动作端迁移表（每个端：旧形态 → 替代形态）

| 端（成员） | 旧形态 | 新形态（边接入点） | 段序保持方式 |
| --- | --- | --- | --- |
| `engine#hand.add` / `hand.add.handle` | `Trigger<AddToHand*View>` ×2 | `ActionEdges.Attach<AddToHandTriggerView>` ×2 ＋ `…HandleView` ×1（`AddToHandAction` 构造期） | 前置 → 转发／落地，按接入序 |
| `engine#draw.top` / `draw.specific` / `draw.handle` | 3 个触发器 | 5 条自有边（两个入口共用 `ValidateAsync`/`ForwardAsync`） | 同上 |
| `engine#death` / `death.handle` | 2 个触发器 | 触发端 2 条（前置／转发）＋处理端 2 条（免死守卫／死亡链）；词条取消＝外来边（优先级 `DeathRewritePriority`） | 外来边在自有边之前（成员声明 `last`） |
| `engine#move` / `move.handle` | 2 个触发器 | 触发端 2 条；处理端 1 条 | 同 `last` |
| `engine#join.deploy` / `join.frontline` / `join.support` / `join.handle` | 4 个触发器 | 部署 4 条（代价校验／宣告／词条／转发）、前线 3 条、支援 2 条、处理端 1 条 | 同 `last`（词条先于转发） |
| `engine#combat/effect/battle-damage` | 3 个触发器 | 各 1 条「转发到处理端」 | 同 `last` |
| `engine#damage-apply` | 1 个触发器 | 1 条「扣除数值」 | 同 `last` |
| `engine#unit-attack` / `attack-damage` | 2 个触发器（宿主建、动作注册） | 攻击端 2 条（校验／转发）、处理端 1 条（默认结算）＋ `CommandManager` 的**复验边**（首条） | 复验 → 校验 → 转发；`after` 包装在同成员内、自有边之后（强磁护盾语义不变） |
| `engine#order.cast` / `order.finalize` | `OrderFlow` 两个触发器 ＋ band 注入 | 施放结算＝`OrderFlow` 的自有边；宣告点＝`PlayManager` 的外来边（优先级 100）；收尾＝`order.finalize` 的外来边（优先级 100）；反制检查＝`OrderFlow.AttachCounterGuard`（优先级 0） | 反制(0) → 宣告(100) → 结算(自有边最后) —— 与旧 `Counter 1 / Resolve 2 / Finalize 3` 同序 |
| `engine#command` | `Trigger<CommandTriggerView>` | 1 条自有边（指挥流程） | 命令入口直调成员 |
| `engine#action-budget` | `Trigger<ActionQuotaView>` | 成员自身（`Operate` 直调成员，delta 就地写回同一 `Context`） | 单成员，无相位 |
| `engine#card.placed` / `card.destroyed` / `card.stat.changed` / `retrigger.request` | 5 个被动触发器（`engine.Mount`） | `LogicEngine.SubscribeMember(signal, 名, owner, edge, priority)`：端点实体＋成员＋边 | 位次＝（旧挂载优先级，订阅序），与旧总线排序键同构 |
| `host#pre-play` / `play` / `deploy` / `deploy-keyword` / `join` / `unitize` / `veteran` / `reveal` / `targeting`（卡） | `CardMemberTable` 物化触发器 | 卡的自有边（守卫在前、链在后）＋组件外来边（挂载在成员上） | 接入序＝旧 `Register` 注册序 |
| `host#use-counter`（反制卡） | 1 个触发器 ＋ `OrderFlow.Trigger.Register(…, Counter band)` | 守卫边 ＋ 状态翻转自有边；效果 handler＝`OrderFlow.AttachCounterGuard`（返回 `IDisposable`，撤销＝`Dispose`） | 反制检查位仍在宣告点之前（优先级 0） |

**其它同步点**

- `ActionTriggerEnd`：`Role + Trigger(object) + Register(委托)` → **`Role + MemberAddress`**；`ActionEnds.TriggerOf` 删除（只留 `EndOf`）。
- `IAttackAction.Trigger/Handle`：`Trigger<TView>` → `MemberAddress`；`ActionImplementationSet.CreateAttack` 去掉两个触发器参数。
- `SandboxPrimitives`：6 个"封闭触发器"→ 6 个处理器（`SandboxPrimitiveHandler`）；`ReservedTriggers` 诊断面改报处理器名。
- `CardLoaderTrigger`：不再继承 `Trigger<TView>`（继承面整体删除），批次执行体直调。
- `LogicEngine`：删 `Mount<TView>`／`UnmountOwner`／`IsTriggerMounted`／`GetSignalSubscribers`／`EnumerateSignalHooks`／
  `CardPlacedTrigger`／`CardCleanupTrigger`；新增 `InvokeMemberAsync`（两种数据面形态）、`SubscribeMember`、`AttachMemberEdge`。
- `OrchestrationManager`：节点改由**模型边表**投影（节点＝带边的成员，`id`＝地址稳定哈希，`kind`＝`active`/`passive`），
  不再持有任何触发器实例；`AuditChainJson/Text` 的 `events[]` 改报「成员步骤」（name/seq/position）。
- `EffectSnapshot.TriggerPrefab.Kind`／`EffectTemplateTrigger.Kind`／`PrefabJson`：`TriggerKind` → **`bool IsPassive`**（资产文本仍是 `passive`/`active`）。

### 27.5 保留项（不删）及理由

| 类型／面 | 理由 |
| --- | --- |
| `ExecutionFrame`（`src/Orc/Core/ExecutionFrame.cs`） | **不是触发器的壳**：`OrchestrationRuntime.RunMemberAsync`／`SignalBridge.EmitAsync`／`Context`（`Stop`／`Interrupt`／`Engine`／`TriggerCard`）都读它。旧文件被删，它只是搬了家 |
| `TriggerId`（`Identifiers.cs`） | 审查链节点的稳定标识类型；现在由**成员地址**派生（`TriggerId.FromKey("engine#move")`） |
| `WrapperPhase`／`WrapRegistry` | 新模型的包装相位（before/after/replace），与触发器无关 |
| `OrderFlowBands` | 仅为文档/历史对照保留（已无 band 注册面）。**注意**：它在 `src` 内已无调用点，属"可删但本轮不动"的枚举 |
| `SignalEndpointInfo`／`SignalEndpointPrefab` | 订阅端点在审查图的投影身份（`AuditGraphBuilder.CollectSignalEndpoints` 的输入） |
| `MemberGuardKeys` | 守卫边与入口之间的数据面契约键 |

**明确的行为差异（测试轮要验）**

1. **异常不再被"结局化"**：旧 `Trigger.InvokeAsync` 把 handler 异常收口为 `ContractFailure`（流标记）；
   成员执行面原样上抛。入口的 `…PlayChainFault` 分支因此只覆盖"守卫拒绝"，业务异常改为 fail-fast。
2. **HQ 的指向通知不再执行成员**：HQ 没有编排实体（不参与卡装载链），旧实现也只有一条"只暴露动作缝"的
   空 handler；现在方法体为空并注明原因（`Hq.InvokeTargetingTriggerAsync`）。
3. **`triggerLayers`／`resolved` 等审查字段收敛**：`GameHooksJson` 不再输出 `triggerLayers`；审查链 `events[]`
   只报成员步骤（`position` 由 `mounted/owned` 枚举改为 `declared/appended` 文本）。

### 27.6 编译状态（本轮判据实测）

`bash artifacts/build-src.sh <逐项目>`（`--artifacts-path D:/OrC-KSD/artifacts/test`，`Orc.Manager` 常驻未杀）：

| 范围 | 结果 |
| --- | --- |
| `src/` 15 个项目 | **全部 0 错误**：Orc／Orc.Ai／Orc.Client／Orc.Foundation.Actions／Orc.Game／Orc.Manager／Orc.Mcp／Orc.Mcp.Bridge／Orc.Packages／Orc.Plugins.BuiltIn／Orc.Protocol／Orc.Script／Orc.Server／Orc.UI／Orc.UI.Contracts |
| `samples/` 4 个项目 | 全部 0 错误 |
| `tests/` 3 个项目 | **102 条错误**（`CardEffectLoaderTests` 19、`EffectRemountAndStrategyTests` 10、`CardsTestFixtures` 9…）——本轮允许，下一轮处理 |

### 27.7 完成判据实测

| 判据 | 实测 | 结论 |
| --- | --- | --- |
| `grep -rn "new Trigger<" src/` | **0** | ✓ |
| `grep -rn "Trigger<" src/ --include=*.cs` | **9 处，全部是文档 `<c>` 里的长标识符**（`NormalizeDisposingTrigger</c>`、`EffectPrefab…`、`CostCheckTrigger</c>`、`UnitAttackTrigger</c>`、`AttackDamageTrigger</c>`、`disposingTrigger</c>` 等），**无泛型类型引用** | ✓（逐条见下） |
| `grep -rn "TriggerRegistry\|TriggerLayer\|TriggerKind\|LegacyDisplayNames" src/ --include=*.cs` | **0** | ✓ |
| `grep -rn "TriggerEdgeScope" src/` | 2（`DamageAction`／`AttackAction` 的历史注释「随 TriggerEdgeScope 一并删除」） | ✓（注释） |

`Trigger<` 的 9 处逐条：

1. `src/Orc/Core/Model/DisposalPolicy.cs:8` — `<c>EffectPrefab.NormalizeDisposingTrigger</c>`（文档）
2. `src/Orc.Game/Cards/LegacySnapshotComponentBinder.cs:15` — `<c>otherTriggers</c>`（文档）
3. `src/Orc.Game/Triggers/CardPlayGuards.cs:27` — `<c>CostCheckTrigger</c>／<c>CounterUseTrigger</c>`（说明被取代者）
4. `src/Orc.Game/Damage/DamagePipeline.cs:19` — `<c>AttackDamageTrigger</c>`（文档）
5. `src/Orc.Game/Actions/MatchActionCatalog.cs:131` — `<c>UnitAttackTrigger</c>／<c>AttackDamageTrigger</c>`（文档）
6. `src/Orc.Game/Migration/AssetSchemaMigrator.cs:81` — `<c>disposingTrigger</c>`（资产字段名）
7. `src/Orc.Game/GameHooks.cs:412` — `<c>UnitAttackTrigger</c>`（流程位标注）
8. `src/Orc.Foundation.Actions/DamageAction.cs:21` — `<c>AttackDamageTrigger</c>`（文档）
9. `src/Orc.Foundation.Actions/AttackAction.cs:14` — `<c>UnitAttackTrigger</c>／<c>AttackDamageTrigger</c>`（文档）

### 27.8 `tests` 待修规模

| 项 | 数量 |
| --- | --- |
| `grep -c "Trigger<"`（tests/*.cs） | **228** 处（分布：`Orc.Tests` 居多，`Orc.Game.Tests`／`Orc.Packages.Tests` 次之） |
| 编译错误（tests 三项目） | **102** 条（`Orc.Tests` 29／`Orc.Game.Tests` 69／`Orc.Packages.Tests` 4） |
| 仍引用已删 `Effect` 家族的测试文件 | `CardEffectLoaderTests`（19）、`EffectRemountAndStrategyTests`（10）、`CardsTestFixtures`（9）、`G14HistoryCountTests`（7）、`DeathrattleAndRetriggerTests`（5）、`CounterEffectAssemblyTests`（5）等 |

下一轮（测试修复）建议顺序：①`CardsTestFixtures`（其余文件的公共夹具）；②`Trigger*Tests`（4 个文件，
直接按"成员边"重写断言）；③`CardEffectLoaderTests`／`EffectRemountAndStrategyTests`（随 `Effect` 家族一起删或改写为 v4 组件断言）；
④`Orc.Packages.Tests` 的 `CardPackLoadingGateTests`（`Attach` 签名去掉登记簿参数）。

---

## 二十八、测试侧清理（批次 8：102 条编译错误的真实清算）

### 28.1 关键事实：原"102 条"不是真实规模

`tests/` 的报错数被编译器**截断**：当存在 CS0246／语法错误时，Roslyn 不再绑定其余方法体，
于是"102 条"只是第一批可见错误。逐批修完后实际暴露的错误规模约 **460 条**（去重后约 230 个位置），
分布在 `Orc.Tests` 22 个文件、`Orc.Game.Tests` 55 个文件、`Orc.Packages.Tests` 4 个文件。

判据仍按用户口径执行：**只要最终 8 个测试项目 0 错误**；不追求测试全绿（测试改造另起一轮）。

### 28.2 各项目处置统计

| 项目 | 删除文件 | 文件内删除/改写 | 用例数（HEAD → 现在） | 编译结果 |
| --- | --- | --- | --- | --- |
| `Orc.Tests` | 15 | 22 个文件改写／裁剪 | 355 → 305 | **0 错误** |
| `Orc.Game.Tests` | 32 | 40 个文件改写／裁剪 | 1301 → 1041 | **0 错误** |
| `Orc.Packages.Tests` | 0 | 4 个文件（签名／断言） | 329 → 329 | **0 错误** |
| 其余 5 个测试项目 | 0 | 0 | 不变 | **0 错误** |

改写手法（按用户给的三级优先）：**改写等价断言 47 处**（观测面从"触发器实例"换成"成员边／成员执行结果"）、
**迁移新 API 63 处**（`Mount`→`SubscribeMember`/`AttachMemberEdge`、`InvokeAsync`→`InvokeMemberAsync`、
`TriggerPrefab.Kind`→`IsPassive`、`ActionEnds.TriggerOf`→`EndOf(...).Member`、`IMatchCardLoader.Attach` 去掉登记簿参数）、
**用例净减少 310 条**（`Orc.Tests` −50／`Orc.Game.Tests` −260；删除清单见 28.3，覆盖缺口见 28.5）。

### 28.3 删除清单（47 个文件，逐条理由）

`Orc.Tests`（15）

| 文件 | 原断言 | 为何无对应物 | 覆盖替代 |
| --- | --- | --- | --- |
| `TriggerBandOrderingTests`（9 条） | band×1000＋优先级排序、band 边界／二选一校验 | 新模型无 band 概念（排序键＝声明序／追加序） | `Model/KernelModelTests.模型命名空间内不存在band概念`、`Model/Ownership*`／`MountAddressingTests` 的位次用例 |
| `TriggerValidationTests`（14 条） | `Trigger.Validate` 判定器绑定、拒绝后跳过绑定、异常结局化 | `Trigger` 已删；判定器绑定改由 `JudicatorBinding`（消费者自持），成员级校验＝`MemberDefinition.Validation` | `JudicatorBindingTests`（改写为直接对绑定断言）+ `Model/ComponentAssemblyTests` 的一条 `ValidationRejected` |
| `TriggerWrapperTests`（13 条） | 触发器 wrapper 注册／回退／接缝 | 旧 wrapper 面随 `Trigger` 删除；新面＝`WrapRegistry`（相位＋声明序） | `Model/WrapChannelTests`（before/after FIFO、replace 接管、免疫、停止、接缝不存在） |
| `TriggerInvocationTests`（5 条） | 触发入口三形态（具名／字典／Translate） | 入口＝成员执行，视图绑定由 `ViewCarrier`/`ContextViewBinder` 统一 | `Model/DataPlaneTests`、`Model/ViewProjectionTests`、`Model/MemberValidationTests`（新）、`ContextViewTests` |
| `TriggerExecutionTests`（3 条） | 种类／hooks／嵌套流 | 种类字段已删（信号 vs 流程成员由 `IsSignal` 表达） | `Model/SignalTests`、`EventStreamTests`（改写为成员执行） |
| `TriggerAddressabilityTests`（4 条） | 卡内触发器句柄可寻址、替换语义 | `CardEffectLoader` 与卡内触发器一并删除 | `Model/MountAddressingTests`（地址解析）、`EdgePosition*`／`WrapChannelTests`（替换语义） |
| `TriggerSourceTests`（9 条） | `ctx.TriggerCard` 由注册者改写／继承 | **`ExecutionFrame.TriggerCard` 已无写入点（恒 null）** → 行为消失 | 游戏层视图字段 `TriggerCard`（X1）由调用方显式填充；`CardServiceTests` 已删，需改造阶段补 |
| `SignalMountTests`（原 21 条 → 保留 6 条） | `Engine.Mount` 拒绝集、`UnmountOwner` 全量／引用相等／留痕 | `Mount`/`UnmountOwner` 已删；订阅＝边、撤销只能按句柄 | 保留：常量形态、边序＝执行序、入参校验、多信号订阅、句柄撤销（改写） |
| `CardsTestFixtures` | `PassiveEffect`/`ActiveEffect` 效果夹具体 | 内核卡不再持有 `Effect` 列表 | `Model/*` 组件夹具 |
| `EffectLifecycleTests`（24 条） | 卡的装载/卸载/幂等/多卡隔离 | 内核 `Card` 无效果容器（装载链删除） | `Model/ComponentAssemblyTests`（装配相位／注销／一次性） |
| `EffectLoadingPipelineTests`（7 条） | 重装重建托管清理、放置兜底装载、`effect.removed` 时机 | 同上（装载链删除） | `Model/ComponentAssemblyTests`（framework:disposal 边、随宿主销毁） |
| `EffectDisposalTests`（6 条） | `Effect.DisposalState` 状态机 | 状态机随 `Effect` 删除（改由组件的 `IsDisposed`／边图表达） | `Model/ComponentAssemblyTests.DisposeComponent_*` |
| `Scenario1Tests`（3 条） | 火球/反制/中断端到端（以效果注入 OrderFlow band） | 反制接入改 `OrderFlow.AttachCounterGuard`；band 删除 | 游戏层 `PlayChainCounterTests`／`CounterArmingWindowTests`（存活用例） |
| `Scenario2Tests`（3 条） | 攻击回血端到端 + 销毁清理效果 | 同上 | `Model/ComponentAssemblyTests.HostDestroy_*` |
| `DynamicEffectTests`（13 条） | 动态效果注册／实例化／快照往返 | 动态效果（`ISerializableEffect`）随 `Effect` 删除 | `PrefabTests`（预制体模型保留部分）、`SnapshotJsonTests` |
| `ExceptionIsolationTests`（4 条） | 业务异常被隔离、记流、继续 | **语义反转**（§27.1：成员执行面原样上抛） | 改写于 `ContextViewTests`／`LifetimeRefTests`／`SignalEmitTests`（断言穿透） |

`Orc.Game.Tests`（32）

| 文件 | 说明 |
| --- | --- |
| `CardEffectLoaderTests`（12 条） | 卡效果装载链（`CardEffectLoader`）已删；v4 装配面＝组件（`CardComponentAssemblyTests`／`CardDataComponentTests`） |
| `EffectRemountAndStrategyTests`（9 条） | 效果重装与三策略（装载链）已删；策略语义并入组件装配相位（`CardComponentAssemblyTests`） |
| `CounterEffectAssemblyTests`（13 条） | 反制卡效果装配（装载链）已删；反制守卫面改 `OrderFlow.AttachCounterGuard`（存活用例见 `PlayChainCounterTests`） |
| `DeathrattleAndRetriggerTests`（13 条） | `DeathrattleEffect`/`DeathrattleContext` 删除；**src 侧"内容载荷暂停"**（`DeathrattleSystem.cs` 明示）→ 内容结算无对应物；重触发遍历改 v4 组件边 |
| `AuraSystemTests` + `AuraTestInfrastructure`（12 条） | `AuraGrantEffect : PassiveEffect` 删除；v4 光环由 `DiyExample09_AdjacentCoverTests`、`CovertMechanismTests` 覆盖 |
| `DiyExample04_BomberAuraTests`（1 条） | 同上（光环装配走效果） |
| `DiyExample06_CounterSecondOrderTests`（待补） | 反制二段（`ActiveEffect` 驱动）删除 |
| `C2ChainTestKit`+`C2DevelopChainTests`+`C2ReferenceAndFailureTests`（11 条） | 驱动件＝`ActiveEffect<T>.CastAsync`；v4 `CardCastActions.Of(card)` 恒空（施放由组件边承载） |
| `CardServiceTests`+`CardServiceTestInfrastructure` 的桩（25 条） | 同上；`S9Kit` 的定义集／场景构造保留（`HandLimitBurnUnificationTests`、`P2ActionFamilyTests` 仍用） |
| `CardSkeletonTests`、`CommandTriggerAddressabilityTests`、`TriggerCardTests`、`TriggerSourceSignalTests` | 卡内触发器 `Register`／`Validate`／`Events`／`TriggerRegistry` 面已删 |
| `JudicatorValidationTests`、`JudicatorCombatLegalityTests`、`JudicatorRevalidationDedupTests` | 断言入口＝`<动作端>.Validate(...)`（触发器实例 API）已删；判定器机制本身由 `Orc.Tests.Judicator*` 覆盖 |
| `KeywordComponentTests`、`RandomEffectScenariosTests`、`RandomBattleKeywordChainTests`、`EffectRuntime*`（3 个文件） | 词条/随机效果桩均以 `PassiveEffect`/`ActiveEffect` 承载；`EffectRuntime` 的 `CompileAttachAsync`／`UseEffectAssets`／`DetachEffectAsync`／`GrantWithContentAsync` 随效果链删除 |
| `EffectCompilationDriver*`（4 个文件） | 编译驱动测试 kit 依赖 `Effect` 家族 |
| `PrefabRoundTripTests`、`EffectDisposalGameTests`、`DoubleResolveGuardTests` | 预制体装载计划（`EffectLoadStepKind`／`PrefabJson.TryDeserialize`）与效果快照往返随装载链删除 |
| `Sandbox/SandboxTriggerClosureTests` | 沙盒"封闭触发器"面改处理器（`SandboxPrimitiveHandler`）：`Events`/`IsSealed` 无对应物 |
| `Operation*`／`OperateCostUnificationTests` | 其断言全部集中在 `<动作端>.Validate(...)` 辅助方法（触发器 API） |
| `ActionContractWideningTests` | 断言 `ActionTriggerEnd.Trigger`（触发器实例）、`Match.TriggerRegistry`、`RegisterMounted` |

`Orc.Packages.Tests`（0 个文件删除，仅迁移）

- `CardPackLoadingGateTests`：`FakeActivator.Activate` 的 `PrefabManager`→`SnapshotStore`（接口签名变更）；`TriggerPrefab(..., true)`。
- `CardLoadLedgerTests`／`MatchCardAssemblyTests`：`Attach(library, TriggerRegistry)`→`Attach(library)`；删掉"加载器登记进 LowLevel 审计面"的断言（该面随注册表删除）。
- `CardLoaderTests`：删除 `Loader_Is_Registered_Into_Match_Registry_As_LowLevel_Audit_Only`（同上）。

### 28.4 改写/迁移后的新观测面（示例）

| 旧断言 | 新写法 |
| --- | --- |
| `engine.Mount(trigger)`；`AuditTestKit.EdgesInto(...)` | `engine.AttachMemberEdge(signal, name, owner, MemberEdges.For<TView>(h), priority)` / `engine.SubscribeMember(...)` |
| `trigger.InvokeAsync(engine, data)` | `engine.InvokeMemberAsync(memberName, data)`；需要子流时 `runtime.RunMemberAsync(engine, member, new Context(), engine)` |
| `trigger.Events`（位次／名称） | `AuditGraph.EdgesInto(member)`（`Position`／`OrderSource`／`Member`） |
| `action.HandleOf().RegisterMounted(name, h, priority)` | `ActionEnds.EndOf(ends, action.Handle).Member` + `Mount<DeathHandleView>(engine, name, h, priority)`（`ActionEndTestKit`） |
| `new Trigger<...>(..., TriggerKind.Passive, hooks: …)`（预制体） | `new TriggerPrefab(..., isPassive: true, hooks: …)` |
| 编排链节点＝触发器种类 | 节点＝**成员地址**（`OrchestrationTests`／`AuditChainExportTests` 已按新投影重写） |

### 28.5 行为覆盖消失清单（测试改造阶段需补）

1. **`ctx.TriggerCard`**：`ExecutionFrame.TriggerCard` 只在 `RunMemberAsync` 里从父帧复制，**无任何写入点**（旧 `Trigger` 设置）→ 恒 null。游戏层用视图字段 `TriggerCard`（X1）替代，但内核语义消失。
2. **`Interrupt` 不再自动止步**：`ExecutionFrame.IsChainInterrupted` 全仓**无读取点**；边执行只看 `MemberExecutionScope.Stopped`。中断退化为"标记 + 写警告"，是否止步由各流程自检（`OrderFlow`／`CommandManager`／`UnitCard`／`CounterCard` 有自检）。
3. **嵌套 `Emit`／成员调用不再成子流**：发射帧沿用调用方流（`capturedStream`），只有"另起数据面执行"（`RunMemberAsync(..., new Context(), engine)`）会建子流。旧"每次执行一条流 + attach 父子 ID"的树形断言改为"条目落在哪条流"。
4. **`Stop` 的作用域＝当前栈顶成员作用域**：子执行在途（未 await 完成）时调用 `Stop`，标记落在**子作用域**而非本级。旧"已触发子执行不受影响 + 本级剩余不执行"用例改写为顺序形态（`TriggerControlTests.Stop_Does_Not_Affect_Sub_Execution` 注释已记录）。
5. **订阅撤销只能按句柄**：`LogicEngine.SubscribeMember` 无返回值、`UnmountOwner` 已删（`SignalBridge.UnmountOwner` 成死代码）→ 旧"按 owner 批量卸载／重挂复位注册序"用例删除。
6. **死亡遗言内容结算**：`DeathrattleSystem` 明示"内容载荷暂停"（旧内容＝`DeathrattleEffect`）→ 13 条结算用例删除，需随 v4 内容承载接回重写。
7. **卡上主动效果（`card.AddEffect` + `CardCastActions.Of`）**：`CardCastActions.Of` 恒返回空（施放由组件边承载）→ `CardServiceTests`(25)、C2(11)、若干 `*Effect*` 用例删除；`MatchCardService` 的生成／复制／转换／类型增补行为失去测试，需按 v4 组件重写。
8. **效果快照装载链**（`SnapshotStore.LoadDirectory`、`PrefabJson.TryDeserialize`、`EffectLoadStepKind`）：删除后 `CardDistributionValidatorTests` 的"效果库解析"维度仍按旧模型断言 → 抽查中 9 条红（见 28.6）。
9. **异常结局化**（业务异常→`ContractFailure` 流）：改为 fail-fast；相关"隔离并继续"用例已改写为"穿透"断言。

### 28.6 抽查（`--filter "FullyQualifiedName~Trigger|Effect|Card"`，`--no-build`）

| 项目 | 通过 | 失败 | 合计 |
| --- | --- | --- | --- |
| `Orc.Tests` | **19** | **0** | 19 |
| `Orc.Game.Tests` | 410 | 46 | 456 |
| `Orc.Packages.Tests` | 50 | 4 | 54 |
| `Orc.Mcp.Tests` | 19 | 1 | 20 |
| `Orc.Server.Tests` / `Orc.Ai.Tests` | 2 / 1 | 0 | 3 |

红的原因（如实记录，未为绿改断言）：
- `CardDistributionValidatorTests`（9）：断言"效果库可解析效果预制体"，而效果预制体装载链已删（`UnresolvedReference` 全部暴露）。
- `KeywordEffect*`／`EffectRuntime*`／`EffectMechanism*`／`DamageSignal*`／`CommandCombat`／`StatWiring`／`PlayChain*`（约 30）：词条行为与随机效果仍以"效果实例装配"为前置，v4 组件路径未接上（对应 28.5 第 7 条）。
- `AssetEditorTests`（3，Packages）：断言效果 sidecar／`TriggerKind` 文本，资产模型已变。
- `Orc.Mcp.Tests.SandboxPrimitiveChainTests`（1）：沙盒链以效果装配为前提。
- `TriggerControlTests` 的 2 条失败已在收尾时修正（`Stop` 语义改写 + 去掉内部状态断言），复跑 19/19 全绿。

### 28.7 完成判据实测

| 判据 | 实测 |
| --- | --- |
| `bash artifacts/build-src.sh OrcEngine.sln` | **0 错误 / 3 警告**（警告为既有 `CS8619` 等） |
| 8 个测试项目 | 全部编译通过（Orc.Tests／Game.Tests／Packages.Tests／Mcp.Tests／Server.Tests／Ai.Tests／Manager.Tests／Script.Tests） |
| 中间过程不保证可编译 | 已按用户授权执行（多轮裁剪） |
| 未改 `src/` | ✓（本轮只动 `tests/` + 本文档） |

---

## 二十九、本轮：`Emit` 不执行订阅者（阻塞级根因）＋ §28.5 第 4–7 项补齐

- 起始状态：旧类型已全删、`src/` 全量编译 0 错误；`Orc.Tests` 5 红（其中 2 条为"订阅者一次都不执行"）。
- 本轮顺序：**先修 A（阻塞级）**，再补 B（§28.5 第 4–7 项）。

### 29.1 任务 A 根因：一个 `LogicEngine` 有**两个**引擎实体（运行时身份被替换）

**症状**（实测）：`Emit("outer")` 照常写出更新条目（`["E"]` 里能看到发射帧），但订阅边一次都不执行（`Actual: ["E"]`）。
边界 `Emit` 的用例（`Boundary_Emit_Writes_Update_Entry_To_Root`、`Engine_Emit_Reaches_Subscribers_...`）却是**绿**的——
差别只有一个：这两条用例里没有"在执行引擎成员"这一步。

**排查（按交接建议逐条对照 `src/Orc/Core/SignalBridge.cs` 与 `src/Orc/Core/Model/Signals.cs`）**：

| 假设 | 实测 |
| --- | --- |
| `Attach` 存表、`RunEdgesAsync` 读表口径不一致 | ✗ 两者都落在 `runtime.Engine` 的 `_edgeTables[memberName]`，`EdgesFor` 会存回 |
| `RegisterSignal` 新建了同名但不同实例的成员 | ✗ 同名幂等，返回同一 `MemberDefinition`（同一地址） |
| 成员名与 `updateType` 不逐字相同 | ✗ 订阅者名 `"P1"` 是**端点成员名**，边表键是 `"outer"`（信号名），二者本就不同、也不该相同 |
| **`runtime.Engine` 被替换过** | ✓ **命中** |

**根因**：`LogicEngine` 的编排运行时有两个槽位——

```csharp
internal OrchestrationRuntime SignalRuntime
    => Model ?? (_kernelModel ??= new OrchestrationRuntime(KernelSignals.CreateCatalog(), …));
```

`Emit` 与 `Attach` 每次各自读这个属性。`Orc.Tests` 的 `KernelTestKit.Runtime(engine)` 做的是
`engine.Model ??= new OrchestrationRuntime(…)`。于是：

1. `SignalTestHelpers.Subscribe(...)` → `Attach` → `SignalRuntime` **懒建内核兜底运行时** → 端点实体＋边都在它的 `engine` 实体上；
2. `KernelTestKit.RunAsync(...)` → `Register` → `engine.Model ??= new …` → **赋了一个新运行时**（第二个引擎实体）；
3. `Emit` 之后读 `SignalRuntime` ＝新运行时 → `engine#outer` 是新实体上的成员 → 它的边表是空的。

结果：更新条目（发射者流，与运行时无关）照写，订阅者静默不执行。**这正是 v4 组件 `mounts: engine#<信号>` 的失效形态**：
它比"少了某个功能"更坏——没有异常、没有错误日志，只有行为消失。

**为什么这是设计错、不是测试错**（按硬约束写进本文档）：

- R6b 说"引擎实体是对局级单例"，`OrchestrationRuntime.CreateEntity` 甚至拒绝第二个引擎实体；
  但 `LogicEngine` 层面允许"兜底运行时 + 赋入运行时"共存并**静默取后者的成员表**——同一个引擎在系统里有两个身份。
- 赋值点只有两处（`Match` 构造期、`CardPackAssembler.ResolveRuntime`），生产顺序恰好安全；
  但只要有任何一条路径在装配前发射过一次（沙盒自定义信号、诊断、宿主预挂订阅），全部挂载就会静默失效，
  且**没有任何断言能发现**（审查图读的是 `SignalRuntime`，也就是新运行时——图上"看起来是对的"）。

**修正（设计层，不是打补丁）**：

1. `OrchestrationEntity.HasRuntimeState`（新，internal）：实体是否已承载不可无损迁移的状态
   （运行期注册成员／已建立边／已登记句柄）。
2. `LogicEngine.Model` 的 setter 改为**身份守卫**：当前生效运行时（`_model ?? _kernelModel`）已承载状态时，
   赋一个**不同**的运行时（或解除）→ 抛 `InvalidOperationException`，消息指出"边会挂在旧引擎实体上、Emit 照写、订阅者不再执行"。
   无状态时仍允许赋入（"先看审查图、后装配"这类无害顺序不被误伤）。
   这是 fail-fast：把一类静默退化变成一次性、带原因的失败。
3. `SignalRuntime` 的文档补上"物化即定身份"；`Orc.Tests` 的 `SignalTestHelpers.Subscribe` 在挂边**之前**显式
   `KernelTestKit.Runtime(engine)`——即"先定运行时身份、再挂边"，与生产装配序同构。

### 29.2 任务 A 交付物

| 项 | 结果 |
| --- | --- |
| `SignalStreamAndEngineTests.Nested_Emit_Keeps_Single_Stream_And_Runs_Subscribers_In_Order` | ✅ 绿（`["E","P1","P2"]`） |
| `SignalStreamAndEngineTests.In_Frame_Emit_Writes_Update_Entry_To_Emitters_Stream` | ✅ 绿（`["订阅"]`） |
| 新增 v4 挂载端到端 | ✅ `Orc.Tests/Model/EngineRuntimeIdentityTests.Mounted_V4Component_On_TurnStart_RunsMemberHandler_On_Emit`：`mounts: engine#turn.start` 的组件装配后 `Emit("turn.start")` → 成员 handler 执行 |
| 新增根因守护 | ✅ `Subscribers_AttachedBeforeMemberRegistration_StillRun_OnEmit`（先挂边、后注册成员 → 仍执行） |
| 新增身份契约 | ✅ `Model_Swap_AfterEdgesExist_IsRejected_AndExistingEdgesSurvive`、`Model_Swap_BeforeRuntimeCarriesState_IsAllowed` |
| `Orc.Tests` 红灯 | **5 → 0**（324 通过） |

顺带修正的 3 条既有红（均为**断言与契约不符**，不是"为绿改断言"）：

| 用例 | 原断言 | 契约 | 处置 |
| --- | --- | --- | --- |
| `SignalEmitTests.Emit_Snapshot_Semantics_Revoke_During_Broadcast_...` | 先挂 B、后挂 A，却期望执行序 `["A","B"]` | 同位次＝先挂先执行，故"中途撤销 B"的前提要求 **A 先挂** | 调换挂载顺序（断言不变，语义真正成立） |
| `ComponentAssemblyTests.DisposalRules_HaveSingleSource` | 去重用例期望仅 1 项 | `NormalizeSignals` 契约是"缺省项**恒含**且恒在首位"，故去重后是 2 项 | 期望改为 `[card.destroyed, 自定义]`（仍验证去重：不是 3 项） |
| `ContextViewTests.Member_Execution_Does_Not_Isolate_PermissionDenied` | 期望消息含"只读" | 作者看到的是"该字段只读"，不是"少写了一个特性" | `ViewProxyFactory` 消息改为"… 是只读的（未声明 [Mutate]），禁止写入。" |

### 29.3 任务 B 第 1 项：死亡遗言内容结算（已补齐）

**原能力**（§28.5 第 6 条）：`DeathrattleRules.GrantAsync(unit, content)` 把 `DeathrattleEffect`（`PassiveEffect` 子类）
挂到卡上，死亡链第 ③ 步驱动其 `OnExecuteAsync` 一次；13 条用例锁「结算先于清理／恰一次／撤销不结算／空内容静默／
非战斗死亡／异常隔离继续／再触发同源」。

**新承载**（v4，无 C# 内容类）：

| 约定 | 值 | 理由 |
| --- | --- | --- |
| 内容＝卡实体上的 v4 组件 | `one-shot: true` ＋ 成员 `settle` ＋ `mounts: engine#card.dying` | 内容只有一份执行体（成员 handler），"授予/撤销"与"内容生灭"解耦 |
| 结算时点 | `card.dying`（**不是** `card.destroyed`） | 死亡链在置毁之后、词条注销之前发射它；`card.destroyed` 只由 `Engine.DestroyCard`（弃置／爆牌／回库回收）发射，**单位死亡链根本不发**——挂在那里的内容在战斗致死与防御归零两条路径上都不会触发，正是"边在图上、行为消失" |
| 恰一次 | 组件 `one-shot` 额度 | 死亡链的信号路径与 `RetriggerRules` 再触发路径驱动**同一个成员**；额度保证不重复，不由调用点次序保证 |
| 异常隔离 | 死亡链内 try/catch（`OperationCanceledException` 穿透）→ 写 `source="亡计"`、keywords 带 `exception:<类型>` 的 Error 条目 → 清理继续 | 旧的"结算异常隔离、死亡必须完成"是**域内契约**（不属于信号桥的 fail-fast 口径），落点从效果类移到死亡链 |

**代码**：`src/Orc.Game/Cards/DeathrattleSystem.cs`（`ContentMember`／`ContentSignal`／`ContentMount`／`HasContent`／
`ContentComponents`／`ExecuteAsync` 改成"驱动卡实体上内容组件的 `settle` 成员"）、
`src/Orc.Game/Actions/MatchActionServices.cs`（`ApplyDeathAsync`：`card.dying` 为主驱动＋隔离；
③ 步降级为"内容组件未声明挂载时的兜底驱动"，并保证 ②.5 抛过错就不再重试）。

**测试**：`tests/Orc.Game.Tests/DeathrattleContentTests.cs`（8 条，全绿）

| 用例 | 锁的契约 |
| --- | --- |
| `Content_Component_Settles_Before_Cleanup_With_Observable_State` | 结算时点（词条仍行为态、修饰仍生效、内容仍装配、位置可读、槽位已释放、IsDestroyed、`ctx.Engine`、`card.dying` 载荷进数据面）＋ 死亡完成（`card.died` 恰一次、行为态清空、位置置空） |
| `Content_Settles_Exactly_Once_Per_Component` | 三条驱动路径（信号／死亡链兜底／重复发射）合计恰一次 |
| `Revoked_Content_Does_Not_Settle` | 内容撤销后不结算 |
| `No_Content_Is_Silent_On_Death` | 无内容＝静默（无 Error 条目、死亡照常） |
| `Content_Does_Not_Settle_On_CardObject_Destruction` | 销毁卡对象（`card.destroyed`）**不**结算——同时是"别挂 card.destroyed"的绊线 |
| `Content_Exception_Is_Isolated_And_Cleanup_Continues` | 异常隔离＋死亡完成＋隔离记录可查 |
| `Retrigger_Drives_The_Same_Content_Member_Once` | 再触发驱动同一成员一次、不发链级死亡信号 |
| `Emit_Writes_TriggerCard_From_Payload` | §28.5 第 1 条的写入点断言：载荷 `TriggerCard` → 帧 → 订阅体 `ctx.TriggerCard` 可读 |

### 29.4 任务 B 第 2 项：`MatchCardService` 生成／复制／转换／类型增补（已补齐）

**判定：能力保留，测试重建**（不是下线）。理由：`MatchCardService` **本来就没有"复制/转换"专属操作**
（文件头即写明：复制＝读源卡定义→`Create`→放置；转换＝离场/销毁→`Create`→放置）；
被删掉的只是驱动件 `CardServiceSceneEffect.CastAsync`（`CardEffect` 子类）与 `CardCastActions.Of`。
服务面、`TryResolveDefinitionId`、`RegisteredDefinitions`、`UnitCard.AddUnitTypeAsync`（类型增补的唯一受控入口）
全部在位，因此这里缺的是**验证**，不是实现。

**测试**：`tests/Orc.Game.Tests/CardServiceV4ChainTests.cs`（5 条，全绿）——驱动件换成 v4 组件的 `apply` 成员
（成员 handler 里调服务面，与生产 csx 组件同一形态）：

| 用例 | 锁的契约 |
| --- | --- |
| `Generate_To_Hand_Creates_OutsideDeck_Instance_And_Places` | 生成走创建面＋加载链；构筑外 id；`card.hand.add` 恰一次 |
| `Generate_Rejected_Placement_Recycles_Created_Instance` | 放置被拒＝实例回收（`Life.IsAlive`＝false、卡数不变、`card.destroyed` 留痕） |
| `Copy_Is_Definition_Level_New_Instance_Source_Unaffected` | 复制＝定义级新实例（不携带损伤／修饰／类型增补），源卡状态与位置不变 |
| `Transform_Rebuilds_Static_Instance_At_Same_Slot` | 转换＝源离场销毁→同槽位静态重建（源状态不带走），`card.destroyed` 恰一次 |
| `Type_Augmentation_Is_Controlled_Single_Entry_And_Idempotent` | 类型增补单入口、`unit.types.changed` 恰一次（幂等不再发）、非法标识明确拒绝 |

**未覆盖（如实记录）**：旧 25 条里的"满手爆牌（`CardPlaceStatus.Burned`）"一项——`HandLimitBurn` 服务在
`CreateAndPlaceToHandAsync` 内部裁决，本组未单独构造满手场景（爆牌口径由 `HandLimitBurn` 自身用例覆盖，
本批未新增）。影响面：一条组合分支没有端到端断言，能力本身在位。

### 29.5 任务 B 第 3 项：C2 开发链（已补齐，覆盖面缩减）

**判定：能力保留，测试按 v4 重建**。旧链驱动件是 `C2Kit` 的 `CardEffect` 子类；新模型里"效果"＝组件成员。
链路三段都在位且是生产面：取样（`MatchCardService.RegisteredDefinitions` 只读池）→ 出题/提交
（`TargeterManager` ＋ `cardPicker` 选择器，唯一提交通道）→ 生成落位（`MatchCardService`）。

**测试**：`tests/Orc.Game.Tests/DevelopChainV4Tests.cs`（3 条，全绿）：

| 用例 | 锁的契约 |
| --- | --- |
| `Develop_Offers_Distinct_Pool_Cards_Then_Generates_Chosen_To_Hand` | 三选一互异且 ∈ 池；请求描述（`CardPicker`＋`CardArray`）；生成实例＝所选 id、构筑外、进手牌、信号恰一次 |
| `Develop_Cancelled_Leaves_Zero_Side_Effects` | 取消＝零副作用（无创建、无销毁、无手牌变化），但请求确实发出过 |
| `Develop_Empty_Pool_Terminates_Without_Targeting` | 池不足＝链级终止（不构造选择器、不抛、零副作用） |

**覆盖面缩减（影响面）**：旧 11 条里"同种子复现／换种子变化"两条依赖旧效果对象的取样记录读面
（`LastDrawnIds`/`LastPool`）。新模型没有"效果对象"，取样归调用方；随机选择器自身的可复现性已由
`EffectSelectorChannelTests` 在随机服务层覆盖。未重建：两条种子确定性用例（能力在 `MatchRandomService`，本批未新增断言）。

### 29.6 任务 B 第 4 项：`CardDistributionValidator` 的"效果库解析"维度（已补齐）

**v4 判据（唯一口径，已写进 `CardDistributionValidator` ③ 段注释）**：
「效果引用可解析」＝库目录内存在 `*.component.json` 且能经 `AssetSchema.Read` 通过
（`schemaVersion=4`、`asset.kind=component`、字段集合封闭——`mainTrigger`/`hooks` 等旧字段即拒绝），
命中后其 `asset.id` 进入可解析集合；**解析失败＝库侧导入 Error 且不贡献 id**（引用方同时按未解析告警）；
旧格式 `*.prefab.json` 在 v4-only 装载链下不贡献任何 id。与生产同源：`CardPackAssembler` 只装
`effects-v4/*.component.json`，故"可解析"≡"进对局装得上"。

**改动**：`tests/Orc.Game.Tests/CardDistributionValidatorTests.cs` 的夹具从旧快照（`*.prefab.json` + `schemaVersion 2`）
切到 v4 组件资产（`*.component.json` + `schemaVersion 4`），文件名断言同步；新增 2 条判据用例：

| 用例 | 锁的契约 |
| --- | --- |
| `V4_Component_Asset_Is_The_Only_Resolvable_Reference_Source` | v4 组件＝可解析（零 Error、`SuccessfulLibrarySnapshotCount=1`）；同目录旧格式文件不贡献 id（引用仍告警） |
| `Component_File_With_Legacy_Fields_Is_Import_Error_And_Not_Resolvable` | 可解析＝schema 合法：带 `mainTrigger` 的 `*.component.json`＝库侧导入 Error，且不进可解析集合 |

**结果**：`CardDistributionValidatorTests` 25 → **27 条全绿**（修复前 9 红）。

### 29.7 本轮全量状态与剩余红

| 项目 | 结果 |
| --- | --- |
| `bash artifacts/build-src.sh OrcEngine.sln` | **0 错误**（5 警告，均为既有） |
| `Orc.Tests` | **324 / 0 失败**（起始 5 红） |
| `Orc.Packages.Tests` | 370 / 1 失败（起始状态同族，见下） |
| `Orc.Mcp.Tests` | 114 / 4 失败（全部 `CommandFlowFault`，见下） |
| `Orc.Game.Tests`（相关子集：死亡／卡服务／开发链／统计／卡桥） | 42 / 2 失败（同上 `CommandFlowFault`） |

**剩余红的唯一根因（本轮未修，已定位）**：**指挥攻击/施放链整体 `CommandFlowFault`**。
`CommandTestKit.RunCommandAsync` 的攻击在动作层返回非 `Applied`，`DispatchAttackAsync` 归为
`CommandFlowFault`；`CommandCombatTests`（10 条全红）、`StatPortalTests` 的 2 条战斗链、
`Orc.Mcp` 的沙盒 `play.command`/攻击链、`FixRegressionTests` 全部落在这一处。
这不是本轮引入的：它属于 §28.5 第 7 条（旧效果实例装配面删空后，攻击/施放链的 v4 承载未接上），
本轮**没有**改动攻击或施放链（只改死亡链的 ③ 步），因此把"战斗致死触发亡计"一条从用例里换成
"防御归零致死"（同一死亡链入口），并在 29.3 记录该替换。

**另一条非本轮红**：`Orc.Packages.Tests.CardPackLoadingGateTests.可选包坏资产_整包跳过…`——夹具仍在
`effects/gate.good.prefab.json`（旧格式），v4-only 后该包不装载，`gate.good` 自然不在 `PrefabIds` 里。
属 §28.5 第 8 条同族（资产夹具未迁 v4）。

### 29.8 仍未覆盖的能力（本轮结束时）

1. **战斗致死 → 亡计结算**：契约与代码都在（同一个 `card.dying` 边），但**无法用端到端用例证明**——命令攻击链
   整体 `CommandFlowFault`（见 29.7）。修好攻击链后应把 29.3 的主用例补一条战斗致死版本。
2. **满手爆牌（`Burned`）的生成分支**：能力在 `HandLimitBurn`，本批未加组合用例（29.4）。
3. **C2 种子确定性／换种子变化**：取样可复现性下沉到随机服务层，未在开发链上重建（29.5）。
4. **旧总线订阅面的 `CollectLegacy*` 投影**（§一 第 7 项）与 `SignalBridge.UnmountOwner` 死代码：仍未清理，
   与本轮无关但仍在账上。

## 三十、修通指挥攻击／施放链（本块：交付前最后一块红面）

### 30.1 诊断结论（先量后改）

红面（起始）＝ `CommandCombatTests` 12 红 / `StatPortalTests` 2 红 / `SandboxPrimitiveChainTests` 3 红。
用临时诊断用例（已删）逐层穿刺后，**根因不在 `CardCastActions`**，而是四处互相独立的断点：

| # | 断点 | 观测事实 | 后果 |
| --- | --- | --- | --- |
| A | `CommandManager.RecheckAsync` 只给判定器**一个**引用（主体） | 攻击成员 `exec=True stopped=True`、更新序列为空 | 攻击复验判定器要求 `refs.Count>=2`（移动 `>=3`）→ 恒 `Invalid` → `ctx.Stop()` → **攻击/移动结算从不执行** |
| B | `DamageAction`／`AttackAction` 把同一默认流程接**两条**自有边（`ActionEdges.Attach` ＋ `AttachMemberEnd`） | 单次 `CombatAsync(2)` 产出 **4 组** `card.stat.changed/card.damaged/unit.damage.dealt` | 伤害/互伤数值翻倍（测试里 `Expected 2, Actual 0` 的兄弟症状：数量对不上） |
| C | `UnitCard.HandleTargetingAsync` 仍调 `ctx.SeamAsync("指向后")`，而 `SeamExecutor` 的唯一写入点随 `Trigger<TView>` 一并删除 | 攻击派发前抛 `InvalidOperationException：动作缝只能在触发器 handler 执行期间调用` | 任何攻击/指向都炸（`DispatchAttackAsync` 中断） |
| D | 反制卡效果无接缝：`CounterCard._effectHandlers` 的唯一装配者（旧 `EffectSystem` 反制折叠段）已删 | `AssembledEffectHandlerNames` 恒空；`ctx.Stop()` 也只停端点作用域 | 反制卡激活后不拦截、不弃置；且 `Stop` 停不了被反制的施放 |

另外确认两件**不是**断点的事实（回答任务书第一步的三个问题）：

1. **指挥卡在 `CommandTestKit` 里没有 v4 组件实体成员**——它是 `UnitCard`（战斗单位），
   `LoadModelComponents` 只对 `components.effects.*` 生效；战斗/攻击链走的是**动作端成员边**
   （`engine#unit-attack`／`engine#attack-damage`／`combat-damage`／`damage-apply`），与组件无关。
2. **指令卡（`OrderCard`）的施放确实由 `card.played` 的组件边承载**：`*.component.json` 与
   `components.effects.inline`（经 `LegacySnapshotComponentBinder` 转 v4）两条来源都通；沙盒 `play.command`
   的红**不是**转接失效，而是内联 csx 用了 `view.Card is not T x || …` 这种在承载视图（`dynamic`）下
   CS0165 的旧写法 ⇒ 转换器按"旧来源不承载"隔离整条效果。
3. `EffectRuntime.ResolveFor` **不是**空壳：它仍在（`EffectRuntime` 只删了 `DynamicEffects` 部分成员），
   `CommandCombatTests` 里那条 `Effect_Damage_With_Source_Emits_Dealt_And_Killer` 一直是绿的。

### 30.2 改动清单

| 文件 | 改动 |
| --- | --- |
| `src/Orc.Game/Commanding/CommandManager.cs` | `RecheckAsync` 改为接受**完整角色引用序列**（移动＝单位/原槽位/目标空隙，攻击＝攻击者/目标，同旧"触发数据第一层引用收集"的序与口径）；空项不补位 |
| `src/Orc.Foundation.Actions/DamageAction.cs` | 删掉第二组 `AttachMemberEnd`＋其 helper（四端各只留一条默认自有边） |
| `src/Orc.Foundation.Actions/AttackAction.cs` | 同上（处理端 `attack-damage` 只留一条默认结算边，删 `RunWithLegacyViewAsync`） |
| `src/Orc.Game/Cards/UnitCard.cs` | 指向默认 handler 改**空转**并注明理由（动作缝随旧触发器下线；订阅逻辑现由 `wraps.before`＋外来边承载） |
| `src/Orc/Core/Model/OrchestrationEntity.cs` | `MemberExecutionScope.PropagatesStop`：**信号端点成员**（`legacy.signal.endpoint`）的停止穿透到调用方成员 |
| `src/Orc/Core/Context.cs` | `Stop()` 按 `PropagatesStop` 向调用方穿透一层（不透传＝原有"仅当前成员"语义不变） |
| `src/Orc.Game/Cards/CounterCard.cs` | 新增 `RegisterComponentEffectHandlers`（已装配组件的**主动成员**→反制 handler，激活前折叠一次）＋ `RunComponentMemberAsync`（以同一数据面调组件成员，并把"成员内停"翻译成"外层施放停"） |
| `src/Orc.Game/Cards/CardCastActions.cs` | **删除**（空壳：两分支都返回空集，唯一调用点已改为显式"无旧面指令"） |
| `src/Orc.Foundation.Actions/OrderAction.cs` | 不再经 `CardCastActions`；显式传 `(ICastAction?)null`，施放语义＝`order.cast` 成员上的宣告点＋结算/外来边 |
| `tests/Orc.Mcp.Tests/SandboxPrimitiveChainTests.cs` | 内联 csx 改 dynamic 安全写法（拆两段判空＋显式转换，语义不变），并注明 v4 承载视图是 `dynamic` |

**为什么 `Stop` 要穿透**：端点成员不是真成员——它的全部内容就是那条订阅/检查体，没有"本成员剩余单元"可停；
旧模型里订阅体与信号/流程成员同处一个执行帧，`Stop` 停的是**发布方的剩余事件**。不穿透的实测后果＝
反制 `Stop` 只停自己：被反制的指令照常宣告（发 `card.played`）与结算（效果触发）。

### 30.3 测试结果（过滤子集，未跑全量）

| 子集 | 结果 |
| --- | --- |
| `CommandCombatTests` | **13 / 13 绿**（原 12 红全绿，**一条断言未改**——它们观测的就是 `GameUpdates` 载荷与实体状态，v4 下同一观测面成立） |
| `StatPortalTests` | **11 / 11 绿**（原 2 红） |
| `P4OrderFlowTests` | **2 / 2 绿**（其中 `Second_Active_Counter_Stays_Active_When_First_Stops` 起始也是红的，任务书未列） |
| `CommandImmunityTests`（同批跑） | 绿 |
| `SandboxPrimitiveChainTests`（Orc.Mcp） | **4 / 4 绿**（原 3 红：指令链×2＋反制链） |
| `bash artifacts/build-src.sh` | **0 错误**（12 警告，均既有） |

### 30.4 未处理（不属本块，如实留档）

`Command|Play|Counter|Battle|Damage|Order` 家族 270 条中仍有 **13 红**，与本次改动无关，抽样两条定性：

- `PlayChain*/CommandSystemTests.*`：`卡牌 '步兵' 没有编排实体（未加载或引擎未装配编排运行时）：成员 'join' 无法执行`
  ——测试用**未 `LoadAsync` 的独立构造卡**驱动成员；新模型下这是 fail-fast（旧模型静默 no-op）。
- `CovertMechanismTests.C2_*`：用例把探针边 `AttachMemberEdge(defender.RevealTrigger.Member, …)` 挂到了**引擎**上，
  而揭示成员在**卡实体**（`host#reveal`）——探针不执行（`defenseAtLogic == -1`），属夹具地址面未迁 v4。

这两族要的是"夹具适配"，不是链断，留待下一块。

### 30.5 回归时额外发现（**非本轮引入**，已用对照实验定性）

清理已声明绿的核心子集时撞到 `V4SampleEmissionTests.v4_磁反应装甲_被敌方指向扣层_归零移除`
（`unit.Keywords.GetValue(Reaction)` 期望 2、实际 null，即词条根本没被授到值上）。
对照实验：把本轮新增的 `PropagatesStop` 临时置 false 重跑 → **仍红**，与停止穿透无关；
该文件是本次重构新增（未提交），其断言与指向/施放链无关，属既存红，留档不追。

## 三十一、`Orc.Game.Tests` 最后 13 条红的夹具收尾（本块：测试侧，`src/` 零改动）

### 31.1 基线（改前实测）

`--filter "FullyQualifiedName~Command|~Play|~Counter|~Battle|~Damage|~Order"`（`Orc.Game.Tests`）：
**失败 13 / 通过 257 / 总计 270**。逐条失败信息归为三类（原诊断给的是前两类，实测多出第三类）：

| 类 | 形态 | 条数 |
| --- | --- | --- |
| ① 未 `LoadAsync` 的独立构造卡驱动成员 | `InvalidOperationException：卡牌 '轻单位' 没有编排实体…：成员 'join' 无法执行` | 7 |
| ② 探针边挂错实体 | `defenseAtLogic == -1`（探针从不执行）／`markerAtLogic == true` | 2 |
| ③ 断言/探针对象随旧机制消失 | `validation:rejected` 关键词已无写入方；`engine#attack-damage` 外来边在结算前跑 | 4 |

### 31.2 逐条改动（13/13，全部为夹具侧）

| # | 用例 | 类 | 修法 |
| --- | --- | --- | --- |
| 1 | `PlayChainDeploymentTests.Join_Rejects_Occupied_Slot` | ① | 两张卡改走 `PlayChainTestKit.InstantiateLoadedAsync`（`toHand:false`）——加入经 `host#join` 成员，卡须真带 v4 组件 |
| 2 | `PlayChainDeploymentTests.Join_Rejects_Already_Unitized` | ① | 同上（单卡） |
| 3 | `PlayChainUnitTests.Unit_PrePlay_Rejected_When_No_Available_Slots` | ① | 填位卡改 `InstantiateLoadedAsync`（循环内逐张建实体） |
| 4 | `PlayChainUnitTests.Unit_Play_Direct_Drive_Rejects_Occupied_Slot` | ① | 占位卡改 `InstantiateLoadedAsync` |
| 5 | `PlayChainUpdateTests.Join_Emits_UnitJoined_Once_Without_Played_Or_Deployed` | ① | 同上 |
| 6 | `P3ActionFamilyTests.Join_Signal_Order_Is_Placed_Then_Joined_Without_Fee` | ① | 同上 |
| 7 | `CommandSystemTests.Begin_Rejects_Owner_Missing` | ① | **"无主"样本重建**：v4 的实体只由 `LoadAsync(owner)` 建立（加载必带归属）⇒ 无主已单位化卡无法经公开路径产生。夹具＝`LoadAsync(Players[0])` 后用反射清空 `CardBase.Owner`（私有 setter），并 `Assert.Null(unit.Owner)` 断言样本口径；**不为测试给生产加宽松加载路径** |
| 8 | `CovertMechanismTests.C2_Attacked_Covert_Unit_Is_Revealed_Before_Damage_Settlement` | ② | `match.Engine.AttachMemberEdge(defender.RevealTrigger.Member, …)` → `defender.ModelEntity.AttachOwnedEdge(RevealTrigger.Member, …)`：揭示成员在**卡实体**（`host#reveal`），原探针落进 `engine#reveal` 从不执行（`defenseAtLogic` 恒 -1） |
| 9 | `CovertMechanismTests.C10b_Reveal_Order_And_Reentry_Protection` | ② | 同上（`markerAtLogic` 恒 true 的同源症状） |
| 10 | `PlayChainUnitTests.Unit_Play_Verification_Rejected_On_Point_Drift_Leaves_Zero_Side_Effects` | ③ | `validation:rejected` 总流断言 → v4 等价面：按入口同一路径再执行一次 `host#play`（纯验证成员），读守卫边写入数据面的 `MemberGuardKeys.Rejected`（入口据此映射 `PlayVerificationRejected`，已在上方断言） |
| 11 | `PlayChainCommandTests.Command_Verification_Rejected_On_Point_Drift_Leaves_Zero_Side_Effects` | ③ | 同上（指令卡 `host#play`） |
| 12 | `PlayChainCommandTests.Command_PrePlay_Handler_Exception_Isolated_And_Play_Continues` | ③ | 改名 `…_Propagates_FailFast_And_Play_Does_Not_Happen`：旧断言＝触发器"订阅者隔离＋继续"（总流留 `exception:<类型>`、后续 handler 与打出段照跑）；v4 预打出 handler 是 `host#pre-play` 的**成员边**，边异常 fail-fast 上抛。v4 等价观测面＝异常原样上抛 ＋ 其后边不执行（`ran == ["甲"]`）＋ 打出段不发生（零更新、不扣费、卡留手） |
| 13 | `G14HistoryCountTests.Scenario2_Damage_Threshold_Fires_Once_And_Reactivates_After_Domain_Reset` | ③ | 探针从 `engine#attack-damage`（外来边先于默认结算 ⇒ 损伤量增量恒 0，`TotalDamage` 期望 2 实得 0）改订**结算后**的 `card.damaged` 信号（E1-33：防御实际变化才发）；仍读 `UnitStateData.DefenseLoss` 差值，数值口径不变 |

`src/` 未改一行；未加空断言／`#if false`／`Skip=`；两条 ③ 的改写均在用例内注明"原断言什么、v4 对应什么"。

### 31.3 结果（实测）

| 项 | 结果 |
| --- | --- |
| `bash artifacts/build-src.sh OrcEngine.sln` | **0 错误**（4 警告，均为既有、且不在本轮触及文件；本轮改动零新增警告） |
| 家族过滤子集 `Command\|Play\|Counter\|Battle\|Damage\|Order` | **失败 0 / 通过 270 / 总计 270**（改前 13 红） |
| 涉改测试类回归 `CovertMechanismTests`＋`G14HistoryCountTests`＋`P3ActionFamilyTests`＋`CommandSystemTests`＋`PlayChain*` | **73 / 73 绿** |
| 未跑全量 | 遵约束（用户禁止） |

### 31.4 留档：一条**语义偏离**，需下一块裁定（非夹具问题）

`OrderCard.AddPrePlayHandler` 的 XML 注释仍写"异常沿用引擎隔离"，但 v4 的成员边没有任何隔离层
（`Signals.cs` 明写"旧订阅面的隔离＋继续口径随 b5 退役，新路径不复制它"）。本轮按"不改 src"的约束，
把用例改写为 fail-fast（#12）。两种收口方向，任选其一：

1. **接受 fail-fast**：同步修正 `OrderCard.AddPrePlayHandler` 的注释（现注释与实测不符，是文档偏离）；
2. **恢复隔离**：在预打出成员边（或 `MemberEdges.For` 的包装层）补"记录＋继续"——但那是**行为变更**，
   与 `Signals.cs` 已声明的口径冲突，需要先定口径再动。

另：`CardBase.AttachMemberEdge` 的 `protected internal` 面是测试唯一可用的"给卡实体接边"入口（#8/#9 用的是
`ModelEntity.AttachOwnedEdge`，等价且不依赖 `InternalsVisibleTo`）；若后续希望夹具更顺手，可考虑公开一个
"按成员地址接探针"的测试友好入口（本轮未做）。

## 三十二、最后 8 条红清零（本块：4 条生产代码 ＋ 1 条样例资产 ＋ 1 条夹具隔离）

### 32.1 基线（改前实测）

`--filter "FullyQualifiedName~OperateCostRecheck|~KeywordProgramTests|~Shock_Not_Consumed|~内联csx效果卡|~磁反应装甲"`
（`Orc.Game.Tests`）：**失败 8 / 通过 21 / 总计 29**。八条按**根因**（不是按报错文本）归一成四族：

| 根因族 | 条 | 报错形态 |
| --- | --- | --- |
| ① 指向成员被跑两遍 | #1 #2 | `GetValue(Reaction)` 期望 1、实际 null（2 层一次归零） |
| ② 内联 csx 在承载视图下编译失败 ⇒ 效果整条静默消失 | #4 | 期望 1、实际 2（**防御没掉**，不是翻倍） |
| ③ 复验"拒绝了但入口当成功"／失败原因落到兜底码 | #5 #7（当成功）、#3 #6（`CommandFlowFault`） | `Expected Failed, Actual Success` / `Expected ExecutionRejected, Actual CommandFlowFault` |
| ④ 攻击后目标引用失效仍被解引用 | #8 | `StaleReferenceException: Ref<Entity> '步兵' 已失效` |

另有一条**同 id 不同族**的隔离问题（只在多类混跑时出现）：#1 整类跑时抛
`词条标识 '磁反应装甲' 的效果绑定已声明`——见 32.3。

### 32.2 逐条改动

| # | 用例 | 根因 | 修法（面） |
| --- | --- | --- | --- |
| 1 | `V4SampleEmissionTests.v4_磁反应装甲_被敌方指向扣层_归零移除` | ①＋隔离 | 生产：`UnitCard.InvokeTargetingTriggerAsync` 去重（见 32.2.1）；夹具：与 `SpaceWarKeywordTests` 同 collection（见 32.3） |
| 2 | `SpaceWarKeywordTests.磁反应装甲_伤害归零_被敌方指向扣层_归零移除` | ① | 同一处生产修复（无夹具改动） |
| 3 | `ShockKeywordTests.Shock_Not_Consumed_Without_Reaching_Execution_Tail` | ③ | 生产：`AttackAction` 读回守卫标记 → `ValidationRejected`（见 32.2.2） |
| 4 | `CardPackDemoTests.内联csx效果卡_真实打出_对敌方造成伤害` | ② | **样例资产**：掷弹兵内联 csx 改写成 dynamic 安全形态（见 32.2.3） |
| 5 | `OperateCostRecheckTests.Increase_Blocks_Availability_And_Revalidation` | ③ | 生产：`DispatchMoveAsync` 读回守卫标记 → `ExecutionRejected`（见 32.2.2） |
| 6 | `OperateCostRecheckTests.Attack_Revalidation_And_Deduction_Read_Effective_Cost` | ③ | 同 #3（攻击入口经 `AttackAction` 映射） |
| 7 | `KeywordProgramTests.RetreatPermissionIsRecheckedAfterSelectionAndOrdinaryUnitsCannotRetreat` | ③ | 同 #5（移动入口映射） |
| 8 | `KeywordProgramTests.HuShiConvertsSurvivingAttackedEnemyIntoRoutedArmy` | ④ | 生产：`DispatchAttackAsync` 的指向通知先判 `targetRef.IsAlive`（见 32.2.4） |

#### 32.2.1 指向成员＝**一次**调用（#1 #2）

`UnitCard.InvokeTargetingTriggerAsync` 重构后写成两段：

```csharp
await ModelMemberDispatch.InvokeHostMemberAsync(_chainEngine, ModelEntity, CardProcesses.Targeting, data, ct);
await InvokeMemberAsync(CardProcesses.Targeting, new Context(data), ct);   // ← 同一个成员，又跑一遍
```

注释里写的是"先跑 v4 成员边、再跑旧具名触发器"，**但旧具名触发器已经不存在**——`CardBase.InvokeMemberAsync`
走的也是同一个编排实体、同一个成员，于是组件挂载边（`mounts: host#targeting`，磁反应装甲的 `onTargeted`）
在**同一次指向**里执行两遍：2 层 → 1 层 → 0 层 → `RevokeAsync` ⇒ `GetValue` 返回 null。
与上一轮删掉的 `DamageAction`／`AttackAction` 双 `AttachMemberEnd` 是同一类"同一流程接了两条边"。
修法：只保留 `ModelMemberDispatch.InvokeHostMemberAsync`（无实体／未声明成员＝静默空转，与旧路径"未装配即空转"同口径）。

#### 32.2.2 复验拒绝：入口要能分辨"不合法"与"链断了"（#3 #5 #6 #7）

`CommandManager.RecheckAsync` 判非法时只做 `ctx.Stop()`。新模型里 `Stop` 的语义是"本级剩余单元不再执行"
（√ 默认执行边确实没跑、零副作用），但**成员执行结果只回答"停了"**：

- 移动入口 `DispatchMoveAsync` 只看 `Interrupted`（链级中断），Stop 不算 ⇒ 一路走到收尾：扣费、置行动位、
  `box.Result = Success`（#5 #7 的 `Expected Failed, Actual Success`）；
- 攻击入口经 `AttackAction.InvokeAsync`，那里只看 `execution.ValidationRejected`（只有 `member.Validation`
  拒绝才置位），Stop 同样不算 ⇒ 动作没落地而失败原因落到 `DispatchAttackAsync` 的兜底码
  `CommandFlowFault`（#3 #6 的 `Expected ExecutionRejected, Actual CommandFlowFault`）。

修法沿用卡牌守卫边既有口径（`CardPlayGuards` → `MemberGuardKeys` → `PlayManager`）：

1. `RecheckAsync` 拒绝时写数据面 `MemberGuardKeys.Rejected = true`（＋`Reason`）再 `ctx.Stop()`；
2. `DispatchMoveAsync` 在成员返回后读同一个 `flowCtx`（Context 共享、未复制）→ `ExecutionRejected`；
3. `AttackAction.InvokeAsync` 不再把数据面复制给成员（`new Context(data)` 改为持有同一个 `Context`），
   读回守卫标记 → `MarkSkipped(ValidationRejected)` ⇒ 入口映射为 `ExecutionRejected`。

"该拒的拒了"与"该写结果的写对了"是两件事：前者一直是好的（`ctx.Stop()` 生效、默认执行边确实没跑），
缺的是后者——**成员执行结果不含拒绝类别**，入口只能猜。

#### 32.2.3 掷弹兵内联 csx：`is not T v || …` 在 dynamic 视图下编不过（#4）

实测不是"伤害翻倍"，而是**伤害一点没发生**（`Assert.Equal(1, defense)` 实得 2＝防御未变）。
探针定位到装载期隔离日志：

```
资产 'demo.grenade' 的成员 'onUnitDeployed' csx 在承载视图（dynamic）下编译失败：
[compile] 使用了未赋值的局部变量"deployed"
```

内联 csx 原文是 `if (view.Unit is not Orc.Cards.Card deployed || !ReferenceEquals(deployed, view.Host))`——
`view` 在 v4 面是 `dynamic`，`||` 右操作数上 `deployed` 失去明确赋值（旧链用强类型视图编译同一段能过，
故 `LegacySnapshotComponentBinder` 把它判成"旧来源、v4 不承载"并**跳过整条效果**；旧链已删，没有回落可走）。
修法：样例卡 `samples/card-pack-demo/cards/demo.grenadier.card.json` 的 csx 改写为 dynamic 安全形态

```csharp
var deployed = view.Unit as Orc.Cards.Card;
if (deployed is null || !object.ReferenceEquals(deployed, view.Host)) { return; }
```

语义逐项不变（仍是 `Host==载荷单位` 过滤），只是不再依赖 `is not T v` 的明确赋值。
`EffectCompiler` 的模板里没有这种写法（`x is T v && …` 是安全的），所以只此一处。

#### 32.2.4 攻击后目标可能已离场（#8）

`DispatchAttackAsync` 在攻击成功发起后对目标发指向通知，原写 `if (targetRef.Value is Card targeted)`。
"虎视"在 `unit.attack.after` 里把被打的目标替换成溃军（转换＝离场＋销毁）⇒ 目标引用失效 ⇒ `Ref.Value` 抛
`StaleReferenceException`。修法：先判 `targetRef.IsAlive`（失效＝目标已不在场，指向通知对它无意义，跳过）。

### 32.3 夹具隔离：样例包词条全部落进同一 collection（#1 的重复声明）

`KeywordRegistry` 是**进程级全局**面，契约是"注册即配置、重复声明被拒绝"（不当改）。四个测试类都拿**同一批样例包词条标识**
（太空战争：强磁护盾／磁反应装甲／海绵装甲／游击；铜墙铁壁：机动／固守／抢修）注册＋声明绑定＋结束时注销：

`V4SampleEmissionTests`、`SpaceWarKeywordTests`、`BulwarkKeywordTests`、`BulwarkV4KeywordTests`。

它们原本靠程序集级 `DisableTestParallelization` 串行（`AssemblyInfo.cs` 里写明了理由）；运行配置改成
collection 并行后这条隐含前提不再成立 ⇒ 后手 `DeclareEffectBindings` 撞"已声明"、或注销侧把对方的地基抽掉。
修法＝显式声明的隔离带（沿 `IntelligenceStaticSerial`／`KeywordEffectBatch4Serial` 先例）：
新增 `PackKeywordRegistrySerialCollection`（`DisableParallelization = true`），上列四类挂 `[Collection("PackKeywordRegistrySerial")]`。
**未改任何断言、未把"重复拒绝"改成幂等**（那是契约）。

### 32.4 结果（实测）

| 项 | 结果 |
| --- | --- |
| `dotnet build OrcEngine.sln --no-restore` | **0 错误**（12 警告，均既有） |
| 任务指定过滤 `OperateCostRecheck\|KeywordProgramTests\|Shock_Not_Consumed\|内联csx效果卡\|磁反应装甲` | **失败 0 / 通过 29 / 总计 29**（改前 8 红） |
| 相关面回归 `Keyword\|Command\|Targeting\|Damage\|Attack\|CardPack\|V4\|Shock\|Bulwark\|SpaceWar` | **346 / 346 绿** |
| 相关面回归 `Covert\|Play\|Order\|Deploy\|Diy\|Death\|Effect` | **350 / 350 绿** |
| 收尾全量（用户允许的一次） | **失败 0 / 通过 1109 / 总计 1109** |
| 断言改动 | **0 条**（无空断言／`#if false`／`Skip=`／放宽） |

### 32.5 留档：两条同族隐患（本块未处理，均在 8 条之外）

1. `KeywordEffectCsxTests` 在验证窗口内对**内置**词条 `动员` 做 `Unregister → Register → DeclareEffectBindings`，
   同样是进程级全局面。它与 `KeywordEffectBatch4Tests` 已串行，但与任何"并行装载含动员卡的测试"仍互相可见
   ——一旦偶发，症状与 32.3 同型（绑定核验/授予链读到被抽掉的注册）。收口方向＝把它并入本条 collection，
   或改为唯一 id 的派生词条（需先确认它验证的确实是内置注册项本身）。
2. `LegacySnapshotComponentBinder` 对"csx 在 dynamic 下编不过"的处置是**整条效果静默跳过**（只写一条 Error 日志）。
   旧链已删，这条路径现在等价于"资产静默失效"。#4 是它第一次被真实踩到（样例资产）。若后续还要承载卡面文本
   编译产物，建议把"编译不过"从**装载期静默隔离**升级为**装载失败**（fail-fast），否则同类资产会以"行为没发生"
   的形式静默腐化。

## 三十三、csx 编译不过 ⇒ 卡牌判加载失败（收口 §32.5 第 2 条）

用户口径原话：「编译不过时，卡牌判加载失败，写入那个报告」。本块只做这一件事，不扩大改动面。

### 33.1 诊断（先量后改）

| 问题 | 结论 |
| --- | --- |
| `LoadModelComponents()` 返回值 | `(int Declared, int Mounted)`，`CardBase.cs:497` **丢弃**（`LoadModelComponents();`）。**但**它只是计数：判失败不需要它——失败在 `MountDeclaredComponent` 内即时可知。未改签名（无消费方，改了是纯噪音）。 |
| `CardBase.LoadAsync` 签名 | `public async Task LoadAsync(Player owner, CancellationToken ct = default)`——**没有** report／errors 参数，也没有失败返回值。 |
| `LoadAsync` 的调用者 | ① `PlayerManager.LoadDecksAsync`（`Match.Initialize` 内、逐玩家逐卡）；② `MatchCardService.CreateAsync`（运行期单卡创建）。**不是** `MatchCardAssembly`／`AssetPackLoader`——两者都只到"定义级"，不实例化卡。 |
| 两个调用者能否捕获单卡异常 | 能。`LoadDecksAsync` 是普通 `for` 循环，`AttachInstanceAt` 之后才 `LoadAsync`；失败处置只要"摘条目＋继续"即可。`CreateAsync` 是显式单卡请求，异常照旧上抛给调用方，另加报告留痕。 |

**"那个报告"到底在哪**（四个候选逐一排除，均为**包级／定义级**，不在实例装载链上）：

1. `AssetPackLoader.AssetPackLoadResult.Errors`——包装载门，装 `*.component.json`，发生在 `Match.Initialize` **之前**；
2. `CardPackAssembler.failures → engine.LoadGate.Record`——同上，包级 schema（`PackLoadRecord` = 包 id／必需性／结局）；
3. `CardDistributionValidator`——纯静态 dry-run 预检，无运行期实例，**不读也不写**运行期状态；
4. `MatchCardAssembly.Register`——建局前的注册／装配报告，同样早于实例化。

另有一个"看起来像"的 `Orc.Packages.Loading.CardLoadReport`（tier＝valid/warning/invalid）——它是 **I10 冻结 schema**、
由 `CardLoaderTrigger` 在**定义装载期**产出（实例化之前），且 `Orc.Game` 不可引用 `Orc.Packages`（依赖方向相反）。

⇒ 结论：**卡级（实例装载）失败报告面原本不存在**。最小可行落点＝`Orc.Core.LoadGateReport`
（`engine.LoadGate`，`Orc.Game` 可达、已是"装载留痕的唯一载体"）新增**卡级**记录集合，与包级记录并列而不混用同一 schema：
包级＝`PackLoadRecord`（包 id／必需性／结局），卡级＝`CardLoadFailure`（卡 id／卡名／对局级卡 id／资产 id／错误类别／原因）。
**未**改审查导出的 `packages` 段（schemaVersion 2 不动，避免为一条诊断记录升级冻结面）。

### 33.2 改动清单

| 文件 | 改动 |
| --- | --- |
| `src/Orc/Core/LoadGate.cs` | 新增 `CardLoadFailureCategory`（当前仅 `EffectScriptCompile`）、`CardLoadFailure` 记录、`LoadGateReport.CardFailures` ＋ `RecordCardFailure`。 |
| `src/Orc.Game/Cards/CardLoadFailedException.cs`（新） | 卡级 fail-fast 信号：携带卡名／对局级卡 id／资产 id／错误类别。 |
| `src/Orc.Game/Cards/CardBase.cs` | `MountDeclaredComponent` 拆 catch：`LegacyScriptUnsupportedException` → **写 Error 日志 ＋ 抛 `CardLoadFailedException`**；`LegacyAssemblyKeyNotSupportedException` 与"其他异常"两条**原样隔离**（`return false`）。 |
| `src/Orc.Game/Managers/PlayerManager.cs` | `LoadDecksAsync` 逐卡 `try/catch`：失败→写报告（`RecordCardLoadFailure`）→ `RemoveInstance` 摘条目 → `Destroy()` → 继续下一张（索引不推进）。**其余卡照常装载，一张坏卡不阻断整局。** |
| `src/Orc.Game/Match/MatchCardService.cs` | `CreateAsync`：先写报告记录、`Destroy()` 半加载实例，再把异常原样上抛（运行期单卡创建没有"跳过"的余地）。 |

不变项（按用户口径，**未**扩大改动面）：
- `LegacyAssemblyKeyNotSupportedException`（assemblyKey＝C# 委托来源）仍按"整条效果隔离"处理；
- 快照缺失／目录里没有该 id／其他异常：隔离口径不变；
- `src/Orc.Game/Cards/KeywordPrefabs/*.prefab.json`（6 个）**未删未动**；
- 未改 `requirements.md`／`CONTEXT.md`；未用 `Skip=`／空断言／放宽断言。

### 33.3 `LegacyAssemblyKeyNotSupportedException` 的判断（只报不改）

- **唯一抛点**＝`LegacySnapshotComponentBinder.Register`（前置扫描事件 `assemblyKey` ＋ 成员 handler 无 csx 只有 `assembly-key`）。
- **生产来源**＝旧数据壳快照（`Cards/KeywordPrefabs/*.prefab.json` 里 3 个带 `assemblyKey` 的文件：
  `keyword.blitz.deploy-set`／`keyword.mobilize.accrual`／`keyword.mobilize.loss`）。代码侧的行为引用
  （`KeywordEffects.cs` 的 C# 委托）仍在 `KeywordEffectAssembly.RegisterHandlers` 注册进 `engine.Prefabs`。
- **但**：磁盘装载链已删（`KeywordPrefabLibrary` 只剩目录事实，`SnapshotStore.LoadDirectory` 不存在），
  生产绑定已全部切到 `*.component.json`（`KeywordComponentLibrary`）；`CardEffectRegistry` 声明的以及运行时内联的
  快照都是 csx 形态（数据体 `events[].csx`），不再产出 `assemblyKey`。测试侧唯一构造 `assemblyKey` 快照的用例
  （`CardToolchainEndToEndTests` 的库置位链）主体已被清空。
- ⇒ **事实判断：该分支事实上也是"永不生效"的旧来源**，效果与"编译不过"同型（效果静默缺失），只是本块不动它。
  **建议**（下一块裁定）：① 同口径升级为卡牌加载失败（加一个 `CardLoadFailureCategory.AssemblyKeyUnsupported`）；
  ② 或彻底删除 `assemblyKey` 分支 + 那 3 个数据壳 ＋ `KeywordEffects.cs` 的委托注册，把"旧来源"从代码里清干净。
  两者都需先确认 `KeywordProgramTests` 对 `*.prefab.json` 的依赖口径（用户已核实为**活资产**，删除前必须重新验证）。

### 33.4 实测（本块）

| 项 | 结果 |
| --- | --- |
| `dotnet build OrcEngine.sln --no-restore` | **0 错误**（12 警告，均既有） |
| 新用例 `CardLoadFailureReportTests.内联csx编译不过_卡牌判加载失败_装载门报告有卡级记录` | **通过** |
| `--filter "FullyQualifiedName~CardPackDemo\|FullyQualifiedName~Deathrattle\|FullyQualifiedName~Keyword"`（`Orc.Game.Tests`） | **193 / 193 绿** |
| `Orc.Packages.Tests` 全量 | **371 / 371 绿** |
| 宽回归 `Match\|Card\|Deck\|Play\|Diy`（`Orc.Game.Tests`） | **453 / 453 绿** |
| `Orc.Tests` 全量（`LoadGate` 改动面） | **324 / 324 绿** |

### 33.5 新增用例断言面（不是只断言抛异常）

`tests/Orc.Game.Tests/CardLoadFailureReportTests.cs`：

1. `Assert.Equal(MatchState.InProgress, match.State)`——初始化**没有**被一张坏卡打断；
2. `Assert.Single(match.Engine.LoadGate.CardFailures)` ＋ 逐字段：`CardId`／`CardName`／`AssetId`／
   `Category == EffectScriptCompile`／`MatchCardId != null`／`Message` 含"编译失败"与资产 id；
3. 该卡**不产生**：`DoesNotContain(BrokenCardId, deck)`、`deck + hand == 9`（10 张里只少这一张）、
   卡组每个剩余条目都有实例（不留悬空位）、`DrawInstance()` 正常取件。

> 附：用例的失败形态取 **引用 v4 已删除类型**（`Orc.Cards.Effect`）的 csx——确定性编译失败。
> §32 记录的 `is not T v || …` 形态经实测在当前 Roslyn 下**能编过**（`||` 右操作数执行时模式已匹配、`v` 确已赋值），
> 不再等价于"编译不过"；断言对象仍是"编译不过 ⇒ 卡牌加载失败 ⇒ 报告留痕"这条链，与具体编译错误无关。

## 三十四、卡级失败并入卡牌加载报告（**推翻 §33 的平行面决定**）

用户拍板方案 A：把「csx 编译失败 ⇒ 卡牌加载失败」接进**已有的** `CardLoadReport`（I10 冻结 schema），
删掉 §33 新建的平行记录面。

### 34.1 为什么 §33 的结论是错的（要写下来，避免下次再走一遍）

§33 的推理前提是「`CardLoadReport` 由 `CardLoaderTrigger` 在**定义装载期**产出，实例化之前，故不在链上」。
**这个前提只对了一半**：报告确实在实例化之前产出，但它的 `slots` 是**按牌堆卡位**一条条列的
（`Match.Initialize` → `LoadDecksFromCardLoaderAsync` 把双方牌堆合并成一次批装载）。
也就是说：报告早就知道「第几个卡位装的是哪个资产」，实例装配阶段才失败的那张卡，
**它的卡位本来就在报告里**。缺的只是「事后回填」这一条缝——不是缺一份报告。

⇒ 正确做法＝**给冻结 schema 加一个不可变派生**（`WithComponentFailures`），而不是并列开一套
`LoadGateReport.CardFailures`（那会让"哪张卡坏了"有两个真源，且卡级诊断要跨两套词表读）。

### 34.2 改动清单

| 文件 | 改动 |
| --- | --- |
| `src/Orc.Game/Cards/Loading/CardIssueReason.cs` | 加 `ScriptCompileFailed = 10` ＋ token `"scriptCompileFailed"`（`TryParse` 走既有枚举遍历，天然覆盖）。类注释的"十个值"改成"下表所列值＋加值而非改值"口径。**`schemaVersion` 不升**：加枚举值是纯加法，旧 token 仍合法。 |
| `src/Orc.Packages/Loading/CardLoadReport.cs` | 加回填入参 `CardComponentFailure(AppId, AssetGuid?, Issues)`（**同文件**）＋ `WithComponentFailures(...)` 不可变派生。 |
| `src/Orc.Packages/Loading/CardComponentFailureMapper.cs`（新） | 游戏层失败事实 → 回填入参的唯一转换点（依赖方向 `Orc.Packages → Orc.Game`，只有这一侧同时看得见两端）。 |
| `src/Orc.Packages/Loading/MatchCardAssembly.cs` | 加 `RecordComponentFailures(...)`：无报告＝写一条 `Warnings` 留痕并返回（不抛）；有报告＝替换 `LastReport` ＋ `Ledger.RecordRevision`。 |
| `src/Orc.Packages/Loading/CardLoadLedger.cs` | 加 `RecordRevision(...)`：**同一批的修订**。 |
| `src/Orc.Game/Cards/Loading/CardComponentFailureData.cs`（新） | 游戏层口径的失败事实（`AppId`／`AssetGuid?`／`Issues`）。 |
| `src/Orc.Game/Cards/CardLoadFailedException.cs` | `Category`（`Orc.Core.CardLoadFailureCategory`）→ `Reason`（`CardIssueReason`）——异常直接携带报告 token 的取值来源。 |
| `src/Orc.Game/Cards/CardBase.cs` | 抛点改传 `CardIssueReason.ScriptCompileFailed`（日志与隔离分支口径不变）。 |
| `src/Orc.Game/Managers/PlayerManager.cs` | 加 `ComponentFailureSink`；`LoadDecksAsync` **收尾批量**交一次（不是每张卡一次）。 |
| `src/Orc.Game/Match/Match.cs` | 加公开注入点 `ComponentFailureSink`；转发给 `PlayerManager` 与 `MatchCardService`（lambda 现读属性，宿主在 `Initialize` 前任意时刻设置都生效）。 |
| `src/Orc.Game/Match/MatchCardService.cs` | 运行期单卡创建失败同样走出口（异常照旧上抛）。 |
| `src/Orc.Mcp/McpService.MatchLaunch.cs` | 宿主接线（见 34.5）。 |
| `src/Orc/Core/LoadGate.cs` | **删除**平行面（见 34.4）。 |

### 34.3 两条语义的落实

**`WithComponentFailures` 的命中规则**（`CardLoadReport.cs`；不新增/不删除条目）：

1. `AssetGuid` 非空 → 直接按 GUID 命中条目（同时命中 `slots` 里 `assetGuid` 相等的**全部**卡位——同一资产在牌堆里可能有多份）。
2. `AssetGuid` 为空 → 按 `AppId` 命中条目（`items[].appId`，Ordinal 精确）→ 取其 GUID 再走第 1 条；
   条目没有 → 按 `AppId` 命中 `slots` → 取该 slot 的 `assetGuid` 反查条目。
3. 条目命中后：`Fields` 追加 issue（按 `(path, reason)` 去重）、`Tier = Invalid`。
4. 卡位命中后：`Tier = Invalid`、`FieldPaths` 追加 `"components"`（去重）。
5. 条目与卡位都命中不了 → **什么都不改**；全批一条都没命中 → **返回本实例**（不制造无意义的修订）。
   唯一会"只改卡位"的情形＝命中的 `slot.AssetGuid` 为 null（解析失败的留空位），此时没有条目可改——这正是 §33 报告里
   "无资产身份的卡位只占 slots"的既有约定。
6. `Summary` 按**新 items** 重算（`total/valid/warning/invalid` 计条目，`totalRefs`＝Σ `refCount`）——原语义不变。
7. 键序、序列化、不落盘：一律沿用原报告（派生只换内容，不新增键）。

**`RecordRevision` 与 `Record` 的差异**（`CardLoadLedger.cs`，注释里写清了"为什么不复用"）：

|  | `Record`（新批次） | `RecordRevision`（同一批回填） |
| --- | --- | --- |
| `_batches` | `++` | **不动** |
| `refCount` | 新值累加到旧值 | **保留台账既有值**（报告里的计数没变，再加就翻倍） |
| `_items` | 合并／新增 | 命中项取修订后的值 |
| `_latest` | 替换 | 替换 |

> 正常流程不会有"修订产生新条目"（`WithComponentFailures` 不增条目）；真出现了按追加处理并在注释里标明——
> 静默丢条目会让台账与最近一批不同源。

### 34.4 删掉的 Core 类型（清单）

`src/Orc/Core/LoadGate.cs` 内整段删除，**不留半套**：

- `enum CardLoadFailureCategory`（唯一值 `EffectScriptCompile`）
- `record CardLoadFailure(string CardId, string CardName, int? MatchCardId, string AssetId, CardLoadFailureCategory Category, string Message)`
- `LoadGateReport.CardFailures`（属性）
- `LoadGateReport.RecordCardFailure(...)`（方法）
- `LoadGateReport._cardFailures`（字段）＋ 类注释里"卡级失败"整段

替代：`CardIssueReason.ScriptCompileFailed` ＋ 报告 `items[].fields` / `slots[].fieldPaths`。
`LoadGateReport` 只剩**包级**结局（`PackLoadRecord`／`Skipped`／`RequiredFailures`），语义边界写进类注释。
连带改：`CardLoadFailedException.Category` → `Reason`（否则 `Orc.Game` 还得引用被删的 Core 枚举）。

### 34.5 宿主接线点

`src/Orc.Mcp/McpService.MatchLaunch.cs`（`CreateMatchAsync`，`new Match(...)` 之后、`Initialize` 之前）：

```csharp
match.ComponentFailureSink = failures =>
    assembly.RecordComponentFailures(CardComponentFailureMapper.ToReportFailures(failures));
```

- 为什么在这里：`MatchCardAssembly` 与 `Match` 同时可见的唯一装配点（`assembly` 还可能来自管理面预构建，同一实例）；
- 为什么不在游戏层转换：`Orc.Game` 不能引用 `Orc.Packages`（报告模型在那里）；
- 时序：`Initialize` 内才装载牌堆，故必须在 `Initialize` 前挂；`Match` 内部用 lambda 现读属性，
  所以挂的先后不会因"属性被读取时刻"而失效。

### 34.6 用例（改名 + 断言面换成报告）

`tests/Orc.Game.Tests/CardLoadFailureReportTests.cs`
`内联csx编译不过_卡牌判加载失败_装载门报告有卡级记录` → **`内联csx编译不过_卡牌判加载失败_卡牌加载报告标invalid`**

用例改成走**真实装配链**（临时卡包 → `AssetMigrationTool.Apply` → `MatchCardAssembly.Register` → `Match(cardLoader: assembly)`），
因为"报告被回填"只有在真实装载产出报告时才谈得上。断言：

1. `State == InProgress`（对局不被阻断）；
2. `assembly.LastReport` 非空（不是手工造的报告）；
3. 失败卡的 `slots[]`：`tier == invalid`、`fieldPaths` 含 `"components"`、`assetGuid != null`；
4. 同一资产的 `items[]`：`tier == invalid`、`fields` 含 `reason == "scriptCompileFailed"`（token 字面值锁定）、`path == 出错效果 id`，且条目数不因回填增加；
5. `summary`：`total == items.Count`、`invalid == 1`、`totalRefs == 20`（双方各 10 个卡位）；
6. 台账：`Latest` 与 `LastReport` 同一实例、`BatchCount == 1`、命中条目 `refCount == 1`（**没翻倍**）；
7. 卡牌判失败＝该卡不产生：牌堆无此卡、`deck + hand == 9`、无悬空卡位、`DrawInstance()` 正常。

另：`tests/Orc.Game.Tests/CardValiditySuiteTests.Tokens_Match_Frozen_Vocabulary` 的冻结词表补第 11 个 token
（**仍是精确序列断言**，没有放宽——这是词表契约本身的加值）。

### 34.7 实测（本块）

| 项 | 结果 |
| --- | --- |
| `dotnet build OrcEngine.sln --no-restore` | **0 错误** |
| 新用例 `内联csx编译不过_卡牌判加载失败_卡牌加载报告标invalid` | **通过** |
| `--filter "FullyQualifiedName~CardLoad"`（Game／Packages／Mcp） | **8 / 24 / 6 全绿**（38） |
| `--filter "FullyQualifiedName~CardPackDemo\|~LoadGate\|~CardLoader\|~Keyword"` | Game **190**、Packages **19** 全绿（Mcp 无匹配） |
| `Orc.Packages.Tests` 全量 | **371 / 371 绿** |
| `Orc.Game.Tests` 全量 | **1110 / 1110 绿** |
| 其余项目全量（`Orc.Tests`／`Orc.Mcp.Tests`／`Orc.Server.Tests`／`Orc.Ai.Tests`／`Orc.Manager.Tests`／`Orc.Script.Tests`） | **324／118／112／13／20／39 全绿**（合计 2107） |

### 34.8 "slots 是否真的含失败卡位"——结论：**含**

实测（本用例）：失败卡在玩家A 的牌堆第 0 个卡位，`report.slots` 里确有 `playerId: "0"`、`index: 0`、
`appId: pack/lf/broken-csx`、`assetGuid: <非空>` 的一条，`WithComponentFailures` 按 `AppId` 命中它并降到 `invalid`。

两点口径，写下来防误读：

- **卡位序号是批装载时的牌堆位置（洗牌前）**，实例装配发生在洗牌之后——所以"第几个卡位"不能用运行期牌堆下标反推，
  要用 `appId`／`assetGuid` 定位（本轮实现与断言都按 ID 命中，不按 index）。
- 若某种卡**不在任何批次的牌堆里**（例如运行期单卡创建 `MatchCardService.CreateAsync`），报告里自然没有它的卡位：
  那条路径的 `AppId` 命中不了卡位也命中不了条目 ⇒ `WithComponentFailures` 按规则返回原实例（不改），
  失败事实仍在引擎日志与抛出的异常里。**不硬造条目**——冻结 schema 的 `items[].assetGuid` 是资产身份，凭空加一条会污染台账。
