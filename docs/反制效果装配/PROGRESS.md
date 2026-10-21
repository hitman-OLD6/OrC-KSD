# 执行进度 — 反制效果装配

- 更新时间：2026-10-21 15:07
- 工具调用计数：约 19
- 当前步骤：S9 完成（S1–S9 全部复核并跑通）
- 总体状态：已完成（全量 2380 通过 / 0 失败 / 0 跳过）

## 已完成

### 基线（S0）

- `dotnet build OrcEngine.slnx`：代码 0 错误，但 `src/Orc.Server/bin` 被外部进程 `.NET Host (29880)` 锁定，
  报 MSB3021/MSB3027（纯 bin 拷贝失败）。按预案改逐项目构建：
  - `dotnet build tests/Orc.Game.Tests` → 0 警告 / 0 错误
  - `dotnet build tests/Orc.Mcp.Tests` → 0 警告 / 0 错误
  - 全量以 `dotnet test OrcEngine.slnx --no-build` 运行（各测试产物均新于源码）。
- 基线口径：2310 ＋ 沙盒原语 56 ＝ 2366。

### S1 装载分支落点（`src/Orc.Game/Cards/EffectSystem.cs`）

- `isCounterHost = card is CounterCard`（只看宿主类，不看 `typeCategory`、不看效果 `kind`）；
  ③ 与 ③b 的 `AddEffect`、④ 的 `MountPassiveEffects` 统一传 `mountMainTriggers: !isCounterHost`；
  ④b 调 `AssembleCounterEffectHandlers`；`injects`／`judicatorWrappers` 仍在 ③b 照常施加。
- 用例：`Counter_CardEffect_NotMountedOnBus_NotTriggeredByCardPlayed` ✔、`Order_And_Unit_EffectLoading_Unchanged` ✔（2/2）。

### S2 一条效果 → 一个 handler（`CardEffectLoader` 预制体段）

- 遍历 `card.Effects`（列表序＝声明序）取 `Snapshot`；`otherTriggers`／`wrappers` 非空 ⇒ 该条不装配、记 Error；
  普通事件＝`band ≠ disposing`（列表序）；空效果 ⇒ Warn、不产 handler。
- 装配期**一次性** `ResolveHandler(ev, typeof(CardTriggerView))` ＋ 强转
  `Func<CardTriggerView, Context, CancellationToken, Task>`（**无 `DynamicInvoke`**；失败按 `signature` 隔离并剔除）；
  运行期 handler 只按序 `await` 已解析条目、共用外层 `ctx`，`ctx.Stopped || ctx.Interrupted` 后短路。
- 用例：`Counter_CardEffect_OneEffectOneHandler` ✔、`Counter_CardEffect_SerialEvents_SharedContext` ✔、
  `Counter_CardEffect_UnsupportedFaces_NotAssembled` ✔（3/3）。

### S3 挂载拆分（唯一内核主线改动）

- `ExecuteMount(bus, mountMainTriggers = true)` → `MountEffect(card, effect, mountMainTriggers = true)` →
  `MountPassiveEffects(bool mountMainTriggers = true)`；默认 `true`、既有调用点零改动，仅反制装载路径传 `false`；
  跳过**只有** `MountMainTrigger` 一行，`IsMounted = true`／`OnMount`／装载完成动作照常（`injects` 可撤销、
  完整销毁链可用）。
- 用例：`Counter_CardEffect_MainTriggerNotMounted_DisposalMounted` ✔、
  `Counter_CardEffect_Destroyed_DisposesFully` ✔（`effect.removed` 恰一次、teardown／卸载钩子各一次、injects 已撤销）、
  `MountPassiveEffects_DefaultBehavior_Unchanged` ✔（3/3）。

### S4 失败与留痕分级（source＝`反制效果装配`）

- Error＝`unsupported-other-triggers`／`unsupported-wrappers`／`viewType`／resolve（含 `ErrorCategory`）／`signature`；
  Warn＝空效果（无快照／无普通事件）；Info＝装配成功（含效果名、预制体 id、折叠事件数与事件名）。均不抛、不阻断。
- 用例：`Counter_CardEffect_NoCallableEvent_WarnsWithoutHandler` ✔、`Counter_CardEffect_Assembled_LeavesInfoTrace` ✔、
  `Counter_CardEffect_ResolveFailure_CardStillActivatable` ✔（3/3）。

### S5 已装配集合可观测面（`src/Orc.Game/Cards/CounterCard.cs`）

- 新增 `public IReadOnlyList<string> AssembledEffectHandlerNames`（`_effectHandlers` 只读投影，装载完成即固定、
  不进 `_activeRegistrations`）；`AddEffectHandler` 的 `name` 传 `effect.Name`。
- 用例：`Counter_AssembledHandlers_ObservableBeforeActivation` ✔（未激活 Assembled=2／Registered=0；
  激活后逐项相等；取消后 Registered 空、Assembled 不变）。

### S6 激活中卡被销毁的清理

- 构造期订阅 `card.destroyed`（只认本卡）→ 注销 `_activeRegistrations` → 置未激活 → 清 `_chargedCost` →
  **不退费** → 撤销订阅；`_destroyed` 守卫使销毁后不可再激活。
- 用例：`Counter_DestroyedWhileActive_UnregistersHandlers` ✔（Counter 段零残留、点数不退还、再激活被拒）。

### S7 沙盒端到端用例升级（`tests/Orc.Mcp.Tests/SandboxPrimitiveChainTests.cs`）

- `CounterChain_PrimitiveBuiltCounter_ActivatesAndOrderResolves` → `CounterChain_PrimitiveBuiltCounter_StopsOrder`；
  断言锁定：指令离手、扣费 1、手牌净 -1（抽牌效果未触发）、`card.played` 计数 0、`card.discarded` ≥ 1、反制卡离手。
  `sandbox.instantiate`／`sandbox.points` 段与 `StopCounterCardJson` 数据零改动。
- 用例：`CounterChain_PrimitiveBuiltCounter_StopsOrder` ✔（1/1，单独复跑通过）。

### S8 既有反制样例的语义归属

- `samples/card-pack-ai/cards/ai.sample.counter.card.json`：`viewType` → `Orc.Game.Triggers.CardTriggerView`，
  csx 入口签名同步（仍为空效果体）。
- 用例：`SampleCounterCard_AssemblesWithoutIsolation` ✔（无 Error、主触发器未挂总线、handler 面就绪）。

### S9 零回归验证

- 全量 `dotnet test OrcEngine.slnx --no-build`：**2380 通过 / 0 失败 / 0 跳过**（连续两次复跑一致）。
  - `Orc.Tests` 370 ✔
  - `Orc.Game.Tests` 1366 ✔（其中本批 14 例：单独 14/14 通过）
  - `Orc.Mcp.Tests` 107 ✔（含 S7 用例）
  - `Orc.Script.Tests` 17 ✔
  - `Orc.Packages.Tests` 356 ✔
  - `Orc.Ai.Tests` 13 ✔
  - `Orc.Server.Tests` 151 ✔
- 对账：基线 2310 ＋ 沙盒原语 56 ＋ 本批新增 14 ＝ 2380。

## 进行中

- 无。

## 未开始

- 无。

## 阻塞与风险

1. **偏差（唯一，需评审确认）**：`Card.AddEffect` 追加可选参数 `mountMainTriggers = true`（默认 true、既有调用点未改，
   仅反制装载路径传 false）。原因：`Add 即装载`（W3-A3）在 ③/③b 的 `AddEffect` 时点即挂主触发器、**早于段 ④**；
   `Effect` 无公开卸载面（`RollbackMount` 为 internal，且会走「未装载」短路破坏完整销毁链），若不在该处传 false，
   被动形态反制效果会先挂上总线（R1 第二条验收失败、构成双触发）。该参数与 S3 同一纪律（只跳过 `MountMainTrigger`），
   未新增开关或第三条挂载路径；文件仍在 Scope 声明的 `src/Orc/Cards/Card.cs` 内。
2. `dotnet build OrcEngine.slnx` 仍因 `src/Orc.Server/bin` 被外部进程 `.NET Host (29880)` 锁定而失败（代码 0 错误）；
   `src/Orc.Server` 源码本批未改动，逐项目构建全绿。外部进程释放后重跑整解构建即可消除该环境噪音。
3. **环境性抖动（非本批引入）**：首次全量运行时 `Orc.Packages.Tests.MatchCardAssemblyTests.In_Match_Card_Resolution_Goes_Through_Bridge_Only`
   因 `KeywordEffectAssembly.VerifyBindings` 报 `sample.entrench.effect` 缺失而失败一次；单跑该用例、单跑该测试项目、
   以及随后两次全量运行均通过 356/356。判定为进程级词条注册／临时库目录的测试间污染（与反制装配路径无交集），
   仅在报告中留痕，未改动相关代码。
4. `Counter_CardEffect_ResolveFailure_CardStillActivatable` 中「csx 签名不符」在**实例化期**即被
   `DynamicEffectFactory` 隔离（其用主触发器视图类型解析全部事件），装配期强转校验是第二道防线、运行期不可达
   （按规则保留，不删）。
5. 销毁卡再激活的拒绝表现为既有判定器 fail-fast（`CounterUseJudicator`：对象须为存活卡），故用例以
   「拒绝 ＋ 状态未翻转」断言，未改判定器契约。

## 下一步

- 无（待评审）。评审如需消除偏差 1，需在 `Orc` 增加「不即时装载的 AddEffect」入口（等价代价，且改动面更大）。
