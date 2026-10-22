"""超时常量（D11.1 唯一真源）。

单测可以在导入后覆盖这些模块级常量（例如把 SELECTION_WAIT 调小），
所有使用点都在调用时读取，故覆盖立即生效。
"""

# ---- 连接 ----
CONNECT = 5.0                 # TCP 连接
INITIALIZE = 5.0              # initialize 握手

# ---- 调用 ----
READ_CALL = 10.0              # 读类调用（tools/list / observe.view / room.info）
READ_RETRIES = 1              # 读类调用重试次数（再失败只提示，不改动作状态）
ACTION_CALL = 30.0            # 动作类调用（play.* / command.* / match.* / mulligan.* / target.*）
ACTION_HINT_AFTER = 5.0       # 动作调用超过该时长即显示「等待服务端…」

# ---- 待决选择 ----
PENDING_WAIT = 5.0            # 响应非 targeting 时等 notify/target.request 的兜底窗口
SELECTION_WAIT = 12 * 60.0    # 选择模式等玩家（服务端 10 分钟按取消处理，这里只是兜底）

# ---- 心跳（服务端 45s 无入站帧即判死）----
HEARTBEAT_IDLE = 20.0         # 空闲多久发一次 ping
HEARTBEAT_MISS = 10.0         # 单次 ping 多久无响应视为一次失败

# ---- 管理器 ----
MANAGER_PING_INTERVAL = 0.2   # 拉起后轮询 manager.ping 的间隔
MANAGER_START = 20.0          # 拉起后等就绪的上限
MANAGER_CALL = 30.0           # 管理面单次调用
SPAWN_CALL = 120.0            # manager.spawn（含实例启动）
START_CALL = 180.0            # match.start（含卡池装配）

# ---- 重试（D11.8 白名单）----
ROOM_ROTATING_RETRY_MS = (200, 400, 800)
ROOM_ROTATING_BUDGET = 2.0
BUSY_RETRY = 2.0
BUSY_RETRIES = 1

# ---- 结果未知（D11.5）----
UNKNOWN_ALIGN = 3.0           # 无帧时主动 observe.view 对齐的等待

# ---- 首帧 ----
VIEW_WAIT = 15.0              # 开局/回座后等首帧视图

# ---- 日志 ----
LOG_MAXLEN = 200              # 每局日志条数上限（R8）
