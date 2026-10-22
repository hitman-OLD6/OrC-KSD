# Requirements — 人类 CLI 对局客户端（文字游戏界面）

为 OrC 服务器提供一个面向人类玩家的终端客户端：以文字游戏式的界面完成一整局 `play` 模式对局（人类坐 seat 0，AI 坐 seat 1），并支持全知观战。它只消费现行协议，不修改服务器、不承载沙盒调试能力。

## Scope

- **Includes**
  - 交互式对局：换牌、出牌（单位/指令/反制）、加入、指挥（移动/攻击/离开）、选靶应答、结束回合、认输。
  - 全屏常驻战场面板 + 战斗日志 + 输入行 + 状态提示条；`--plain` 行式退路。
  - 指令补全与可行动作提示（数据来源＝服务端视图帧的 `actions`）。
  - 开局编排：实例创建、连接申请、座位批准、开局。
  - 断线重连（seatToken）、房间轮换后的重新接入提示。
  - 观战模式 `watch`：不绑座的全知查看者。
- **Excludes**
  - 沙盒/调试面：`card.write`、`card.load`、`sandbox.*`、`editor.*`、`observe.snapshot`、`observe.segments` 等作者向能力。
  - 资产编辑、卡牌创作、卡包制作。
  - 远程联机（跨主机）、鉴权/账号体系。
  - 图形界面（卡图、动画、音效）。
  - 对手 AI 的策略开发（沿用服务端 `Orc.Ai` 现有实现）。

## Constraints

- 运行环境：Windows 终端（Windows Terminal / Git Bash）+ Python 隔离 venv（优先 3.13，缺省用当前解释器）；全屏界面依赖 `prompt_toolkit`，行式界面不依赖任何第三方库。
- 中文文案、CJK 宽字符对齐必须正确（不得出现列错位）。
- 仅连接回环地址（`127.0.0.1`），不引入额外鉴权层（管理权＝进程主人）。
- 严格遵循 `docs/服务器管理器-长期生命周期/客户端接入指南.md` 的现行协议；**不修改** `src/Orc.Server`、`src/Orc.Mcp`、`src/Orc.Protocol` 的行为。
- 单局内不阻塞服务端：任何工具调用都必须有超时与可读错误，不得让界面无响应。

## Requirement Items

### R1: 交付形态与位置
- Status: Confirmed
- Scenario/Trigger: 用户需要启动并维护这个客户端。
- Behavior: 作为 Python 工具交付在 `tools/` 下，由仓库内脚本入口启动（不进入 `OrcEngine.sln`）；使用隔离 venv，不污染系统 Python。
- Acceptance:
  - [ ] 仓库内存在可直接运行的入口（形如 `tools/orc_play.py` 或 `tools/orc/` 包 + 启动脚本）。
  - [ ] 启动前不需要用户手工 `pip install`（入口能自检依赖并给出可读指引，或由脚本自动使用隔离 venv）。
  - [ ] 不新增/修改任何 `.csproj`，`OrcEngine.sln` 不变。
- Terminology: [客户端](../CONTEXT.md)

### R2: 座位拓扑与先手
- Status: Confirmed
- Scenario/Trigger: 开始一局 play 对局。
- Behavior: 人类固定坐内部 `seat 0`（即口语「1 号座」，玩家索引 0，先手）；AI 固定坐 `seat 1`。界面上「我方」永远是 seat 0。
- Acceptance:
  - [ ] 人类连接以 `seat:0` 申请并获批，AI 座在实例创建时以 `kind:"ai"` 声明。
  - [ ] 第一回合的当前行动方是人类。
  - [ ] 界面「我方/对手」标题与 seat 0/1 映射固定，不随帧内容变化。
- Terminology: [座位](../CONTEXT.md)、[控制器](../CONTEXT.md)、[查看者](../CONTEXT.md)

### R3: 界面形态
- Status: Confirmed
- Scenario/Trigger: 对局进行中，玩家观察与输入。
- Behavior: 默认全屏常驻布局——对手区在上、我方区与手牌在下、战斗日志滚动区、底部固定输入行与提示条；每次动作后原地重绘，不滚屏。提供 `--plain` 切换为行式追加输出（每条命令后打印一块战场快照，保留完整历史）。
- Acceptance:
  - [ ] 默认模式下，终端画面不因动作而整体向上滚动，战场区域位置固定。
  - [ ] 输入行与提示条始终可见，AI 行动期间也可见（显示「对手行动中」）。
  - [ ] `--plain` 下每条命令输出一块自包含的战场文本，可被完整重定向到文件。
- Terminology: [人类 CLI](../CONTEXT.md)

### R4: 范围限定为 play 模式
- Status: Confirmed
- Scenario/Trigger: 创建服务器实例。
- Behavior: 实例以 `mode:"play"` 创建；CLI 不申请 `debug` 角色，不调用任何沙盒/编辑器工具。
- Acceptance:
  - [ ] `manager.spawn` 请求中 `mode` 恒为 `play`。
  - [ ] `session.attach` 的 `roles` 只含 `controller` 与 `viewer`。
  - [ ] 工具清单中不出现 `card.*`/`sandbox.*`/`editor.*`/`asset.*` 的可用入口。
- Terminology: [沙盒模式](../CONTEXT.md)、[调试 API](../CONTEXT.md)

### R5: 开局编排
- Status: Confirmed
- Scenario/Trigger: 用户运行 `play` 且尚未有可用对局。
- Behavior: CLI 完成「确认管理器在线 → 创建实例 → 连接并申请座位 → 批准座位 → 开局」的全过程；**管理器视为外部常驻依赖**（不在线时给出启动指引，`--launch-manager` 可选自启）；实例由本次 CLI 会话拥有，正常退出时销毁，`--keep-instance` 保留。任一步失败给出可读原因与下一步建议，不留下半开的实例。
- Acceptance:
  - [ ] 成功路径无需用户手敲任何管理命令。
  - [ ] 管理器未运行时，给出明确提示（含启动方式），不静默失败。
  - [ ] `--launch-manager` 时由 CLI 拉起管理器并在退出时保持其常驻。
  - [ ] 正常退出后实例被销毁；`--keep-instance` 时实例保留且打印实例标识与端口。
  - [ ] 中途失败时，已创建的实例被清理或明确告知其实例标识供后续处理。
  - [ ] 开局完成后进入界面，`phase` 显示为换牌或进行中。
- Terminology: [管理器](../CONTEXT.md)、[实例标识](../CONTEXT.md)、[房间](../CONTEXT.md)

### R6: 指令集与补全
- Status: Confirmed
- Scenario/Trigger: 玩家在输入行键入指令。
- Behavior: 每个动作都提供**英文短词与中文别名两套写法**（如 `play` / `出牌`、`attack` / `攻击`、`end` / `结束`），共用同一套参数语法（短词 + 纯数字序号，语义由参数位置决定）。支持**内联参数**一步到位（`play 3 2` ＝ 手牌 3 → 插入位 2），也允许**省略参数**：当动作需要额外选择时进入引导（`play 3` 后列出候选插入位）。补全候选携带中文卡名与关键数值。
- Acceptance:
  - [ ] `help`/`?` 列出全部指令，含中英两种写法及一句话说明。
  - [ ] 中英写法调用的是同一执行路径，行为与错误提示一致。
  - [ ] 全屏界面：输入前缀后按 Tab 出现候选，条目含中文卡名与关键数值；行式界面（`--plain`/管道）：参数缺失时打印同一批候选并等待输入，信息量一致。
  - [ ] 内联参数完整时一次执行到位；**动作需要额外选择时**才进入引导，不需要选择时直接完成；任何情况下不发送不完整请求。
  - [ ] 输入非法序号时给出范围提示，不崩溃、不误发请求。
- Terminology: [指令](../CONTEXT.md)、[引导](../CONTEXT.md)

### R7: 选靶与选牌交互
- Status: Confirmed
- Scenario/Trigger: 服务端推送 `notify/target.request`（指令卡选目标、部署选位、换牌选牌等）。
- Behavior: 面板切换到统一的「选择」模式：按序号列出候选（名称/类型/血量/描述），支持多选并实时显示已选数量与 `min..max` 约束；`ok` 提交、`x` 取消；超时或过期给出明确提示并退出选择模式。换牌阶段复用同一套选择界面。
- Acceptance:
  - [ ] 收到待决请求后立即切换选择模式，输入行提示变为选择语法。
  - [ ] 多选请求支持一次输入多个序号（逗号或空格分隔），超出 `max` 或少于 `min` 时拒绝并提示。
  - [ ] 取消后回到普通输入，且不留下悬挂的 pending 状态。
  - [ ] 收到 `notify/target.expired` 后提示已过期并清除候选。
- Terminology: [指向](../CONTEXT.md)

### R8: 战斗日志
- Status: Confirmed
- Scenario/Trigger: 服务端推送 `notify/segment` 或视图帧变化。
- Behavior: 把事件段翻译为人类可读的中文叙述（谁对谁做了什么、伤害/死亡/抽牌/费用变化），按发生顺序追加到日志区；只展示，不参与裁决。
- Acceptance:
  - [ ] 一次出牌/攻击至少产出 1 条可读叙述，含行动者与对象名称。
  - [ ] 日志保留最近 N 条（N 可配置，默认 ≥200），可 `log` 指令回看。
  - [ ] 未识别的事件以原始**事件名**降级显示（形如 `[unit.foo] 摘要`），不丢失。
- Terminology: [战斗日志](../CONTEXT.md)、[事件流段](../CONTEXT.md)

### R9: 卡组来源
- Status: Confirmed
- Scenario/Trigger: 申请座位时需要提交卡组与卡包声明。
- Behavior: 默认使用 `samples/card-pack-ai`（5 种，覆盖单位/装甲/炮兵/指令/反制），把该包全部卡种按资产 id 字典序循环填充到 39 张作为卡组，并以 `deckRules: none` 创建实例；`--pack <目录>` 更换卡包，`--deck <json>` 用外部卡组覆盖。
- Acceptance:
  - [ ] 无参数启动时能直接开局（自动选包 + 自动生成 39 张卡组）。
  - [ ] `--pack` 指定的卡包被用于 `match.start` 的卡池装配。
  - [ ] `--deck` 指定的卡组被原样用于 `session.attach`。
  - [ ] 卡组非法时，服务端的 `deck-invalid` 被翻译为可读提示并指出问题项。
- Terminology: [卡包](../CONTEXT.md)、[卡组](../CONTEXT.md)

### R10: 观战模式
- Status: Confirmed
- Scenario/Trigger: 用户运行 `watch`。
- Behavior: 以不绑座的 `viewer` 接入（全知），渲染双方战场与手牌明细，不提供任何动作指令；不干扰正在进行的对局。只做实时观战，不做录像/回放。
- Acceptance:
  - [ ] `watch` 能连上已存在的实例并显示双方手牌。
  - [ ] `watch` 下输入动作指令被拒绝并提示「观战模式只读」。
  - [ ] `watch` 退出不影响对局与其他连接。
- Terminology: [全知](../CONTEXT.md)、[查看者](../CONTEXT.md)

### R11: 断线与房间轮换
- Status: Confirmed
- Scenario/Trigger: 连接断开后重连；或对局结束触发 `notify/room.rotated`。
- Behavior: 座位令牌保存在 `.orc-manager/cli/session.json`；重新运行时凭令牌自动恢复座位（无需管理员再批准）。令牌失效（`invalid-token`）时提示并回退到重新申请。收到轮换后清除旧对局内容、显示终局结果，并给出「重开 / 退出」二选一。
- Acceptance:
  - [ ] 断线后重新运行 CLI 能凭本地保存的 seatToken 直接回到原座位。
  - [ ] 令牌失效时给出可读提示并回退到重新申请流程。
  - [ ] 轮换后界面清除旧对局内容，显示终局结果与新房间状态。
- Terminology: [座位令牌](../CONTEXT.md)、[房间轮换](../CONTEXT.md)

### R12: 对手形态
- Status: Confirmed
- Scenario/Trigger: 创建对局时决定 seat 1 由谁驱动。
- Behavior: 固定使用服务端托管的 AI 座位（`kind:"ai"`），CLI 不承担 AI 驱动、不提供「外部 agent 接入」参数；外部 agent 接入走管理器既有链路，不属于本客户端。
- Acceptance:
  - [ ] `play` 开局后 AI 能自动换牌、行动，无需人工干预。
  - [ ] CLI 的参数表中不存在任何"对手连接/对手令牌"类选项。
- Terminology: [托管客户端](../CONTEXT.md)
