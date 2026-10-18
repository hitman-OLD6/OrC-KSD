# Requirements — 引擎共享变量（跨效果数据袋）

让引擎托管一份**对局级**（＝引擎实例级）的变量存储，使一个效果写入、另一个效果读取（典型："造成等同于所有友方已摧毁单位数量的伤害"），无需每个效果各自订阅并累计。

> 状态：Confirmed（需求已对齐）

## Scope

- Includes:
  - 内核新并列容器 `EngineVariables`（`LogicEngine.Variables`），与 `Cards` / `References` 同层；
  - 任意 `object` 值的写入/读取/删除/清空；
  - `null` 值、空白键、同 key 异类型的拒绝语义；
  - csx 经 `ctx.Engine.Variables` 读写（无需改沙箱与白名单）；
  - `CONTEXT.md` 术语条目与单元测试。
- Excludes:
  - 卡面文本解析与效果 DSL 原语扩展（"所有友方之前被摧毁的单位"如何被解析出来，不在本次范围）；
  - 内建"已摧毁单位"池与累积逻辑（由游戏层/卡作者订阅 `card.died` 自建）；
  - 快照 JSON、事件流、MCP 工具、UI 投影的暴露；
  - 事务回滚与所有权自动撤销；
  - 线程安全（沿用引擎单线程语义）；
  - 修改 `EngineReferences` 的既有契约与 `Count` 口径。

## Constraints

- P2：读写同步语义，不引入后台/异步清理。
- 加性面：既有公共 API 零破坏。
- csx 侧命名须避开沙箱文本屏蔽片段（`Registry` / `Environment` / `System.IO` 等）——`EngineVariables` / `Variables` 均不命中。

## Requirement Items

### R1: 共享变量承载
- Status: Confirmed
- Scenario/Trigger: 任一效果（C# / 预制体 / csx）需要在自身生命周期之外保存/读取数据时。
- Behavior: 对局级唯一一份共享变量存储（＝引擎实例级；`Match` 每局 `new LogicEngine()`）——对局内所有效果看到同一份；不同对局/引擎实例互不可见；随对局丢弃而丢弃，无进程级残留。
- Acceptance:
  - [ ] 效果 A 写入的值，效果 B 在同一对局内可读到。
  - [ ] 两个对局（两个引擎实例）之间互不可见。
  - [ ] 引擎实例丢弃后不残留（无进程级静态存储）。
- Terminology: [引擎共享变量](../../../CONTEXT.md)

### R2: 读写键与冲突语义
- Status: Confirmed
- Scenario/Trigger: 多个来源使用相同 key 写入；或使用空白 key。
- Behavior: key 为自由字符串（Ordinal 比较、空白拒绝），不强制命名空间；同 key 同类型写入＝覆盖（后写生效）；同 key 写入不同运行时类型＝拒绝并抛明确异常（不静默覆盖）；读取缺失＝`TryGet` false / `Get` 抛。
- Acceptance:
  - [ ] 同 key 同类型二次写入后，读到的是后写值。
  - [ ] 同 key 先 `int` 后 `string` 写入被拒绝，原值保持不变。
  - [ ] 空白或 `null` key 写入被拒绝。
  - [ ] 未写入的 key：`TryGet` 返回 false，`Get` 抛异常。

### R3: 值形态与读写契约
- Status: Confirmed
- Scenario/Trigger: 效果写入/读取共享变量。
- Behavior: 值为任意 `object`（引用共享，允许 `List<Card>` 这类实例集合）；`null` 值拒写；`TryGet<T>` 缺失或类型不符返回 false（不抛）；`Get<T>` 缺失或类型不符抛明确异常；`GetOrCreate<T>` 缺失时创建并写入，存在但类型不符＝抛。
- Acceptance:
  - [ ] 可存 `List<Card>` 并在另一效果中取回同一实例。
  - [ ] `TryGet<int>` 读一个 `string` 值返回 false，不抛。
  - [ ] `Get<int>` 读一个 `string` 值抛明确异常。
  - [ ] `GetOrCreate<List<Card>>` 同一 key 二次调用返回同一实例。

### R4: 失败与回滚
- Status: Confirmed
- Scenario/Trigger: 效果装载失败、动作作用域失败、效果销毁。
- Behavior: 共享变量不参与任何回滚——写入即生效；效果装载失败/销毁不撤销其已写入的值；无原值快照、无事务栈。
- Acceptance:
  - [ ] 效果在装载期写入后装载失败，值仍存在。
  - [ ] 效果销毁后，其写入的值仍存在。
  - [ ] 不存在的 key 被 `Remove` 时返回 false，不抛。

### R5: 观察面与权限
- Status: Confirmed
- Scenario/Trigger: 快照导出、事件流消费、MCP 工具、UI 投影。
- Behavior: 共享变量全部不出现在上述观察面（仅内存内可读）；csx 可自由读写（`ctx.Engine.Variables`），沿用弱沙箱前提。
- Acceptance:
  - [ ] 快照 JSON 中不含共享变量条目。
  - [ ] 写入/删除不产生任何事件流段。
  - [ ] csx 处理器可写入并由另一 csx/C# 处理器读到。

### R6: 清理
- Status: Confirmed
- Scenario/Trigger: 主动清理某个键；沙盒调试或复用引擎实例时清空。
- Behavior: `Remove(key)` 幂等（不存在＝false）；`Clear()` 清空全部；引擎实例丢弃即整体消失。
- Acceptance:
  - [ ] `Remove` 后该键读取为缺失。
  - [ ] `Clear` 后 `Count` 为 0。
  - [ ] 两个引擎实例的 `Clear` 互不影响。
