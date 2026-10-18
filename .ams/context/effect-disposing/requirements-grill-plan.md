# Requirements Grill Plan — 效果 disposing 触发器

> 聚焦：销毁语义边界、责任分配、兜底时机、数据面承载与兼容性。

## Unresolved Questions

### Q1: disposing 触发器的形态与"默认必选"的落地方式 {Completed}
- 已定：与主触发器并列的独立 Trigger；由 Effect 基类默认提供（空逻辑 + 默认兜底），作者可覆写。

### Q2: "销毁"包含哪些动作、容器移除与 effect.removed 的归属 {Completed}
- 已定：调用即完整销毁（逻辑＋容器移除＋恰一次 effect.removed）；重复调用幂等空转。

### Q3: 提前调用与兜底调用的重入/幂等状态机 {Completed}
- 已定：进入即停用；重复/重入空转；异常仍完成销毁；主动效果同一路径。

### Q4: 兜底调用时机的形态 {Completed}
- 已定：复用总线更新名字符串，与主触发器 hooks 一致；disposing 触发器为 Passive 触发器。
- 显式销毁入口委托 disposing；装载失败回滚不走 disposing。

### Q5: 未声明兜底 hooks 时的默认行为 {Completed}
- 已定：默认强制补 `card.destroyed` 且不可移除；`effect.removed` 禁止作兜底（配置期 fail-fast）；`match.ended` 后续待办。

### Q6: 注销责任分层（框架最终撤销层是否保留） {Completed}
- 已定：双层。核心句柄撤销必须执行（框架幂等最终层）；手写 disposing 只处理额外副作用。
- 最终层时机：disposing 之后、容器移除之前。

### Q7: disposing band 的形态 {Completed}
- 已定：框架提供默认存在、排序永远最后、不可覆写的保留 band；特殊需要可由作者注册到普通 band 放更前。

### Q8: 保留 band 的写入权限、默认执行与专门方案下的存在性 {Completed}
- 已定：受限注册面（只接受销毁 handler）＋ `OneShot` 自动注册；作者也可手动注册。
- 保留 band 为 Trigger 内部保留槽，所有触发器（含专门 band 方案）默认存在。

### Q9: 数据面承载与 schema 版本 {Completed}
- 已定：`root.disposingTrigger` + `root.oneShot`；`EventPrefab.band ∈ {default, disposing}`；schema 3，接受 2 读入补默认，写出 3。
- 预制体不支持任意专门 band 前置；代码侧支持。

### Q10: 既有生命周期触发器与 disposing 触发器的关系 {Completed}
- 已定：disposing 取代生命周期触发器；C# 被动效果 MainTrigger=null（纯注入型）；动态效果业务主触发器保留并并列。
- 主动效果：施放触发器不挂载，disposing 挂总线接收兜底。

### Q11: 失败与可观测性语义 {Completed}
- 已定：公开 DisposalState（Mounted/Disposing/Disposed）；开始/完成 Info、重复 Debug、失败 Error；`dispose:early` / `dispose:fallback` / `dispose:signal` 区分来源；不提供完成度标记。
- 偏差：`<hook 名>` 未实现（Bus Emit 不向订阅者提供 updateType），记为后续待办。

### Q12: 兼容与迁移范围 {Completed}
- 已定：允许破坏性整理；`OnUnmount` 保留为 disposing 默认作者事件；`ExecuteUnmount`→`ExecuteDispose`；`AddUnmountCleanup`→`AddTeardownRegistration`；`CleanupEffect`、`_lifecycleTrigger` 删除；既有 C# 效果零改动。

### Q13: OneShot 的适用范围与判定点 {Completed}
- 已定：仅对有业务主触发器的效果有效，无主触发器声明＝装配期 fail-fast；C# 注入型效果用 Manual。
- 主动效果 OneShot＝施放完成即销毁。
