# Requirements Grill Plan — 游戏层：手牌选靶与前线入场

> 本轮聚焦：需求文本中"选择数字大于已有的手牌要返回 cancel"与"战场触发器"两处歧义；并以现有代码行为基线做交叉校验。

## 代码事实基线（用于交叉校验）

- 现有选择器模板已含 `SelectorTemplates.Hand`（`MultiReferenceSelector`，点选），但**没有**封装成"从手牌选牌"的可复用 targeter 流程；换牌流程在 `MulliganManager` 内自建候选（`player.Hand`、min 0、max＝手牌数、域判定"仍在手牌"）。
- `PlayManager.JoinUnitAsync(card, slot)` 走加入链（不扣费、不走部署词条、`unit.joined`），目标槽位只要为空即可——**代码上已可传前线槽**。
- `PlayManager` 的**打出/部署**候选线由 `DeploymentLines` 决定：默认仅己方支援线；仅当词条程序 `deploy.frontline` 且前线无敌方单位时追加前线。
- `EffectRuntime` 公开动作面**没有**任何"加入战场"能力（无 join/slot 相关方法）；效果要加入单位目前只能走代码侧 `PlayManager.JoinUnitAsync`。
- 动作目录已存在 `join.battlefield`（`IJoinBattlefieldAction`：部署/加入两触发端＋共享处理端），即"加入战场触发器"在**动作层**已有同名物。

## Unresolved Questions

### Q1: "选择数字大于已有的手牌"指哪个数字？ Completed
- 定案：指请求的**最小张数** `min`。`min > 手牌数` ⇒ 自动取消（不发起交互）。
- 追加定案：`min == 手牌数` 时**不得静默提交**，仍发起交互、保留玩家手动取消的机会。

### Q1a: `max > 手牌数` 的处置 Pending
- 推荐：收敛为上界＝手牌数（"至多 3 张"在只剩 2 张时应能选 1~2 张），不取消。

### Q2: "加入战场触发器"的承载形态 Completed
- 定案：效果可调用的**两个具名触发端**——加入前线／加入支援阵线（不是普通动作方法包装）。
- 追加定案：加入前线的敌方占线门禁＝**触发端报错**，策略判定由效果处自行先判（复用 `move.frontline-enemy`）。

### Q3: 落位如何选定 Completed
- 定案：默认**最左**；具守护的单位自动落 **HQ 右侧**；调用方可选传入**目标序号**（插入后的位置）。

### Q3b: 落位规则的边界与优先级 Completed
- 定案：显式序号 > 守护 > 默认最左；序号越界＝失败（不钳制）。
- 纠正：列表语义下**不存在"已在最右"**——未满 5 个元素右侧总可插入；满 5 个＝warning 留痕＋静默失败（非 UB，正常对局可出现，不抛）。

### Q3a: 「报错」与「静默失败」是否同一形态 Completed
- 定案：都不抛、都返回可判别失败；留痕级别区分——敌方占线＝Error，无可用位置＝Warning。

### Q4: 手牌选靶的产物形态与使用方 In Progress
- 候选：①返回 `Ref<Entity>` 列表（贴 targeter 契约）；②返回 `Card` 列表（贴效果侧）；③两者分层提供。
- 使用方：csx 只能经 `EffectRuntime` 门面；C# handler 可走公共辅助。

### Q5: 阵线模型改造的落地口径 Completed
- 语义已定（R4）：有序单位列表、容量 5（HQ 计入）、插入不居中重排、候选＝N+1 临时空隙。
- 代码基线：`BattleLine.GetInsertionGaps()` 已是「N 占位 → N+1 空隙、占用变化即失效、满员为空」；`FormationGap` 注释已声明「never a physical empty slot」。
- 真正不符三点：①`TryOpenGap` 仍按槽位索引**居中重排**（`start=(Capacity-N-1)/2`）⇒ HQ 恒居中；②`Slot` 是固定 5 格实体、`Index` 恒 0..4，被 70+ 源文件/45+ 测试文件当"位置"使用（UI `slotIndex`、MCP、沙盒、AI、相邻/范围/守护判定）；③预部署候选由 `PlayManager.DeploymentLines` 直接算，不经判定器。
- 待选口径：a＝硬切（`Slot` 退场，`FormationGap` 成为唯一位置句柄；`slotIndex` 对外降级为"当前序号"）；b＝语义硬切但保留 `Slot` 作为动态位置视图（外部类型不变）；c＝只改部署选位（保留槽位模型）。

### Q3a: 「报错」的形态 Pending
- 候选：①失败结果对象（沿用 `PlayResult`/`ActionResult` 口径，不抛）；②抛异常（fail-fast）。
- 既有口径：玩家动作级失败＝结果对象；装配性错误＝异常。

### Q4: 手牌选靶的产物与使用方 Pending
- 候选：①返回 `IReadOnlyList<Card>`；②返回 `Ref<Entity>` 列表；③同时提供"选中即弃"等预设语义。
- 使用方：仅效果侧（`EffectRuntime`）还是任意 C# handler 可用。
