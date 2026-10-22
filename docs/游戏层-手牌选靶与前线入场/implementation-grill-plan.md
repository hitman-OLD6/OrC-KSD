# Implementation Grill Plan — 游戏层：手牌选靶与前线入场

> 本轮聚焦：阵线硬切后的"位置表示法"与连带签名改造顺序。

## 已定实现约束（来自需求）

- 阵线＝有序单位列表，容量 5（HQ 计入），插入不居中重排，候选＝N+1 临时空隙（含首尾）。
- `Slot` 退场；`FormationGap` 是唯一位置句柄；预部署候选由判定器产出（可被包装器改写）。
- 落位＝显式序号 > 守护（HQ 右侧）> 默认最左；失败一律不抛（敌方占线 Error / 满员 Warning）。
- 手牌选靶：`min > 手牌数` ⇒ 零交互 `Cancelled`；`min == 手牌数` ⇒ 必须交互；`max` 收敛。
- 实施顺序：R4/R4a（阵线＋判定器）→ R3b/R3（加入战场）→ R1/R2（手牌选靶）。

## 代码基线（决定实现难点的硬事实）

- `FormationGap : Entity`——targeter 候选要求 `Ref<Entity>`，所以位置句柄**必须**仍是 Entity，不能退化为纯 `int`。
- `UnitStateData.Position` / `HqStateData.Position` 类型是 `Slot?`（`DataComponents.cs:32`、`HqComponents.cs:21`），被 70+ 处读取（`state.Position?.Occupant`、`slot.IsEmpty` 等）。
- `UnitCard.HandleUnitizeAsync` 在卡侧把 `state.Position = slot` 与 `slot.Place(unit)` 双写；`TryOpenGap` 返回 Slot 供这条链使用。
- 相邻/范围/守护/拦截全部建立在「`Slot.Index` 差 1」或「`line[i]` 下标」上（`GameEnvironment`、`CommandManager`、`Combat*` 判定器）。
- 外围 `slotIndex` 有两条来源：`Slot.Index`（沙盒 `ShapeTools`）、投影期反查（`McpService.Projection.TryLocateSlot`）；UI 用 `Hq.Position?.Index`。

## Unresolved Questions

### I1: 位置存什么（`Position` 字段改造） Completed
- 定案 a：改存「所在线」（`BattleLine?`），序号一律 `line.IndexOf(card)` 现算；落地＝`line.Insert(gap.Ordinal, card)`。

### I4: 加入战场两触发端挂在哪 Completed
- 定案 b：动作层 `Join` 拆为 `JoinFrontline` / `JoinSupportLine`（共享 `Handle`）；`EffectRuntime` 只转发调用。
- 关联定案：触发端视图载荷 `Slot` → `FormationGap`（沿用快照失效拒绝）。

### I2: 批次闭包与"仓库红色窗口" In Progress
- 事实：删除 `Slot` 后，任何引用它的文件（70+ 源文件）都会编译失败——S1/S2 与外围适配实质上不可分割；"阶段末绿"只有两种达成方式：一次性硬切到底，或允许过渡期双轨（与 Q5 定案冲突）。
- 候选：a＝一次性硬切（S1→S7 连续改完，期间红、最后一次全绿）；b＝过渡双轨（先并存再删）；c＝先做 S6（手牌选靶，完全独立）缩短红窗口。
- 事实：现状移动＝清旧槽 → 写入目标**空槽**（不挤开任何人）；列表模型下不存在"空槽"，只有插入点。
- 候选：a＝移动也用插入语义（任意空隙，右侧元素顺移）；b＝移动只允许末尾追加（不挤开）；c＝移动限定为端点两种（最左/最右）。

### I6: `SelectorNames.Slot`（"slot"）是否改名 Completed
- 定案 b：改名（`"deployGap"`），同步 `SelectorNames`／`SelectorTemplates`／`KindOf`／`AiPilot`／示例桥。

### I5: 手牌选靶如何表达"取消" Completed
- 事实：`SelectAsync` / `BeginTargetSelectionAsync` 都以空集表示取消/失败/无候选，无法区分"玩家取消"与"空选成功"。
- 候选：a＝沿用空集（与既有口径一致但不可辨识）；b＝返回结果对象（三态＋牌列表）。

### I3: 部署候选判定器的名字与签名 Completed
- 事实：判定器可持 `Battlefield` 只读引用（`MoveFrontlineEnemyJudicator` 先例）；内置名注册在 `Match.Initialize` 固定段，且 `GameHooks` 有转发常量与 `_names` 列表需同步。
- 候选：a＝`deploy.position.candidates`，`(Player owner, UnitCard unit) → IReadOnlyList<FormationGap>`（跨线整体，含"能否上前线"决策）；b＝`deploy.gap.candidates`，`(BattleLine line, UnitCard unit)`（只算一条线，线决策留在 PlayManager）；c＝拆两个判定器（线决策＋空隙）。

### I2: 顺序与编译闭包 Pending
- 硬切会让 `src` 大面积编译失败，是否允许"分阶段提交、期间仓库不可编译"（每阶段末尾必须绿）。

### I3: 判定器名与签名 Pending
- 新判定器名（受控常量）、输入载荷（线/玩家/单位）、输出（`FormationGap` 列表）、注册归属（内置段/玩家域）。

### I4: 加入战场两触发端的挂载形态 Pending
- 是 `EffectRuntime` 上的两个 `Trigger<JoinBattlefieldTriggerView>` 属性，还是复用 `IJoinBattlefieldAction.Deploy/Join` 并加"加入前线/加入支援阵线"两个入口。
