# Requirements Grill Plan — 引擎共享变量（跨效果数据袋）

> 聚焦：**"全局"术语仲裁**、作用域与生命周期、"复用托管接口"的准确含义、key 命名域与冲突、值形态与可观察性、与既有历史计数/查询面的分工。
>
> 基线事实（已核）：
> - `LogicEngine.References`（`src/Orc/Core/EngineReferences.cs`）是引擎实例级 key→object 登记面，契约声明 key 恒为 N 格式 GUID。
> - 已有非 GUID key 先例：`KeywordProgramRuntime.Session`（`src/Orc.Game/Cards/ProgramKeywordComponent.cs:380`）。
> - csx 可经 `ctx.Engine` 取得引擎（`src/Orc/Core/Context.cs:64`），但沙箱文本级屏蔽 `Registry` / `Environment` 字样（`src/Orc.Script/CSharpScriptSecurityOptions.cs:43-65`）。
> - 游戏层无"已摧毁单位集合"，现成来源＝`MatchHistoryService.GetEntries(CardDied)` 事件流遍历（`src/Orc.Game/Match/MatchHistoryService.cs:12-28`）。
> - `Match.cs:160` 每局 `new LogicEngine()` ⇒ 对局级＝引擎实例级。

## Resolved Questions

### Q1: "支持这个语法"的落点层级 {Completed}
- 已定：**仅承载面**——引擎托管字典 + C#/csx 读写 API；DSL/卡面解析不在本次范围。

### Q2: 作用域与生命周期（"全局"仲裁） {Completed}
- 已定：**对局级**（＝引擎实例级）；随对局丢弃，无进程级残留，无需开局清空钩子。
- 术语：称「引擎共享变量」，不用"全局"（项目内"全局"＝进程级）。

### Q3: 承载形态（"复用引擎托管接口"的准确含义） {Completed}
- 已定：**新并列容器**——`LogicEngine.Variables`（类型 `EngineVariables`），与 `Cards` / `References` 同层；**不**塞 `EngineReferences`。
- 理由：不污染 GUID 契约与 `References.Count` 诊断口径；不触发"同对象单键"限制。

### Q4: 值的形态与读写契约 {Completed}
- 已定：任意 `object` ＋ 泛型读写（`Set` / `TryGet<T>` / `Get<T>` / `GetOrCreate<T>`）；引用共享。
- 已定：`null` 值拒写；空白键拒写；`TryGet<T>` 类型不符＝false；`Get<T>` 缺失/类型不符＝抛；同 key 写不同类型＝拒绝并抛。
- 事实约束：卡牌销毁时 `References.Unregister(card.Id)`（`CardLifecycle.cs:301` → `LogicEngine.cs:141`）⇒ 引用键字符串不能承载"已摧毁单位"，值须握 `Card` 实例引用。

### Q5: 失败与回滚语义 {Completed}
- 已定：**不纳入任何回滚**。写入即生效，装载失败后的残留由作者负责；不引入事务栈、不记录原值快照。

### Q6: key 命名域 {Completed}
- 已定：自由字符串（空白拒绝、Ordinal 比较）；不强制命名空间；文档约定 `源.用途`（如 `friendly.destroyed.units`）。

### Q7: 观察面与权限 {Completed}
- 已定：不进快照 JSON、不发事件流、不新增 MCP 工具、不进 UI 投影；csx 可自由读写（沿用弱沙箱既有前提，`Orc` 程序集已在白名单）。

### Q8: 是否内建语义池 {Completed}
- 已定：**不内建**。"已摧毁单位"的累积由游戏层/卡作者订阅 `card.died` 自行写入列表（列入 Excludes）。

### Q9: 与既有模式的分工 {Completed}
- 已定：与「历史计数／自写监听 handler」**并存**，不替代；不强制约束（不进 P1 校验面）。

### Q10: 清理 {Completed}
- 已定：提供 `Remove(key)`（幂等、返回是否存在）与 `Clear()`；引擎实例丢弃即整体消失。

### Q11: 并发/线程模型 {Completed}
- 已定：沿用引擎单线程语义，不加锁（与 `EngineReferences` 一致）。

### Q12: 命名与术语 {Completed}
- 已定：类型 `EngineVariables`，属性 `LogicEngine.Variables`；术语「引擎共享变量」写入 `CONTEXT.md`。
