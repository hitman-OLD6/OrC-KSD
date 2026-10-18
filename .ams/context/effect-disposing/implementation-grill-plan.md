# Implementation Grill Plan — 效果 disposing 触发器

> 聚焦：内核保留槽与状态机怎么落地、销毁入口如何收敛、数据面与游戏层迁移。

## Unresolved Questions

### Q1: 保留 band 槽的排序与注册面 {Completed}
- 已定：独立 `IsDisposalSlot` 标记、双段排序、`RegisterDisposal`、撤销复用；保留槽允许多事件。

### Q2: disposing 触发器的构造、视图类型与兜底过滤 {Completed}
- 已定：固定 `Trigger<CardEventView>`；内建不可撤销守卫事件＋静默停止；匹配规则按载荷引用识别，全局 hook 到达即匹配；预制体 viewType 必须为空或 CardEventView。

### Q3: Effect 挂载/销毁链重接线与状态面 {Completed}
- 已定：四态；`ExecuteDispose` 自完成停用/逻辑/teardown/移除/发射；`CardLoadout` 只做归属校验与调用；OneShot 在 ExecuteMount 成功后注册、回滚撤销；RollbackMount 保持 NotMounted。

### Q4: 销毁入口收敛与自指 {Completed}
- 已定：处理器收窄为 UnregisterCard + effect.removed 幂等兜底；card.destroyed 由各 disposing 自响应；RemoveEffectSilently 仅回滚用。

### Q5: OneShot 的注册时机与纯注入型判定 {Completed}
- 已定：被动动态 ExecuteMount 后注册，纯注入型＋OneShot＝实例化失败；主动效果构造期注册；C# 无主触发器＝构造期 fail-fast。

### Q6: 预制体 schema 3 的归一化与加载计划 {In Progress}
- 默认 disposingTrigger 在 EffectPrefab 构造期生成还是 PrefabJson.FromDto 生成。
- 读入 2 后内存快照归一化为 3；写出恒 3。
- `EventPrefab.Band` 校验位置；`EffectLoadStepKind.Dispose` 枚举扩展的破坏面。

### Q4: 销毁入口收敛与自指 {Pending}
- `RemoveEffect` → `DisposeEffect` 的调用链；`effect.removed` 发射后被 `CardLoadoutProcessor` 再次收到如何空转、如何避免无谓留痕。
- `card.destroyed` 批量销毁与逐效果发射的顺序。

### Q5: 动态效果 OneShot 与注入型接线 {Pending}
- 业务主触发器无 hooks 时如何表达"有主触发器但未挂载"；OneShot 的装配期校验点。
- 主动效果 OneShot 在 CastTrigger 保留槽上的执行。

### Q6: 预制体 schema 3 兼容细节 {Pending}
- 读入 2 的补齐逻辑放在 `PrefabJson` 还是 `EffectPrefab` 构造；`EventPrefab.Band` 的合法性校验位置。
- `BuildLoadPlan` 新增步骤种类是否属于破坏性枚举扩展。

### Q7: 游戏层与测试迁移清单 {Pending}
- `MainTrigger` 的既有使用点（审查链/装载/测试）；日志关键词断言迁移范围。
- 新增测试放在哪个文件/项目。
