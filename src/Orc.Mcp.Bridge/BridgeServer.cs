using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Mcp.Bridge;

/// <summary>
/// stdio MCP 服务循环（无头沙盒 S5）：对外 <c>initialize</c> / <c>tools/list</c> / <c>tools/call</c>，
/// 对内转调 <see cref="IBridgeTransport"/>。
/// <para>
/// **stdout 只承载 JSON-RPC 帧**：本类只往传入的 <see cref="TextWriter"/> 写协议行，诊断一律 stderr。
/// 桥专属工具只有 <c>seat.state</c> 与 <c>seat.segments</c>；服务端工具清单原样透传（不增删、不改授权）。
/// </para>
/// </summary>
public sealed class BridgeServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "orc-mcp-bridge";
    private const string ServerVersion = "1.0.0";

    private static readonly JsonObject SeatStateSchema = BuildSchema("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """);

    private static readonly JsonObject SeatSegmentsSchema = BuildSchema("""
        {
          "type": "object",
          "properties": { "sinceSequence": { "type": "integer" } },
          "additionalProperties": false
        }
        """);

    private readonly IBridgeTransport _transport;
    private readonly SeatStateTracker _state = new();
    private bool _stop;

    public BridgeServer(IBridgeTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.NotificationReceived += _state.OnNotification;
    }

    /// <summary>进程退出码：<c>0</c> 正常收尾（含服务端退出）/ <c>2</c> 令牌失效。</summary>
    public int ExitCode { get; private set; }

    /// <summary>跑 stdio 循环（EOF＝正常收尾）。</summary>
    public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (!_stop && !ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await HandleFrameAsync(line, output, ct).ConfigureAwait(false);
        }

        return ExitCode;
    }

    private async Task HandleFrameAsync(string line, TextWriter output, CancellationToken ct)
    {
        JsonNode? frame;
        try
        {
            frame = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(output, null, -32700, "Parse error").ConfigureAwait(false);
            return;
        }

        if (frame is not JsonObject request)
        {
            await WriteErrorAsync(output, null, -32600, "Invalid Request").ConfigureAwait(false);
            return;
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(method))
        {
            await WriteErrorAsync(output, id, -32600, "Invalid Request").ConfigureAwait(false);
            return;
        }

        // 通知（无 id）不需要响应（initialize 之后的 notifications/initialized 等）。
        if (id is null)
        {
            return;
        }

        switch (method)
        {
            case "initialize":
                await WriteResultAsync(output, id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion,
                    },
                }).ConfigureAwait(false);
                return;

            case "ping":
                await WriteResultAsync(output, id, new JsonObject()).ConfigureAwait(false);
                return;

            case "tools/list":
                await HandleToolsListAsync(output, id, ct).ConfigureAwait(false);
                return;

            case "tools/call":
                await HandleToolsCallAsync(output, id, request, ct).ConfigureAwait(false);
                return;

            default:
                await WriteErrorAsync(output, id, -32601, $"Method not found: {method}").ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleToolsListAsync(TextWriter output, JsonNode? id, CancellationToken ct)
    {
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "seat.state",
                ["description"] = "读取本座位精简状态（phase/turn/yourTurn/pendingSessionId/手牌数/段缓冲统计）。",
                ["inputSchema"] = SeatStateSchema.DeepClone(),
            },
            new JsonObject
            {
                ["name"] = "seat.segments",
                ["description"] = "读取桥缓冲的事件流段（只读，最多 200 段；超出按 sinceSequence 截断并返回 truncated）。",
                ["inputSchema"] = SeatSegmentsSchema.DeepClone(),
            },
            new JsonObject
            {
                ["name"] = "seat.board",
                ["description"] = "读取精简战场摘要（约 1–2 KB，替代 observe.view 的全量投影）："
                    + "回合/相位/费用/HQ、双方战线（uid＋名称＋攻血＋slot）、空支援槽与空前线槽、"
                    + "手牌（handIndex＋isCounterArmed；handCount 为手牌总数、armedCounterCount 为已武装反制数）、"
                    + "可打出集合、攻击预览、可移动目标（toZone=front/support）、待决会话号。",
                ["inputSchema"] = SeatStateSchema.DeepClone(),
            },
        };

        try
        {
            foreach (var tool in await _transport.ListToolsAsync(ct).ConfigureAwait(false))
            {
                JsonNode schema;
                try
                {
                    schema = JsonNode.Parse(tool.InputSchema) ?? new JsonObject();
                }
                catch (JsonException)
                {
                    schema = new JsonObject();
                }

                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = schema,
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] tools/list 失败：{ex.GetType().Name}: {ex.Message}");
            _stop = true;
            ExitCode = 0;
        }

        await WriteResultAsync(output, id, new JsonObject { ["tools"] = tools }).ConfigureAwait(false);
    }

    private async Task HandleToolsCallAsync(
        TextWriter output,
        JsonNode? id,
        JsonObject request,
        CancellationToken ct)
    {
        var name = request["params"]?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            await WriteErrorAsync(output, id, -32602, "params.name 缺失。").ConfigureAwait(false);
            return;
        }

        var arguments = request["params"]?["arguments"] as JsonObject ?? new JsonObject();

        var connection = await _transport.EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (connection != BridgeConnectionState.Ready)
        {
            // 服务端退出 → not-connected（桥退出 0）；令牌失效 → unauthorized（桥退出 2）。
            ExitCode = connection == BridgeConnectionState.TokenRejected ? 2 : 0;
            _stop = true;

            var code = connection == BridgeConnectionState.TokenRejected ? "unauthorized" : "not-connected";
            var message = connection == BridgeConnectionState.TokenRejected
                ? "座位令牌失效（实例可能已重启），请重新读取 agent.token 并重启桥。"
                : "未连接服务端（端口已关闭）。";

            await WriteToolResultAsync(output, id, Failure(code, message), isError: true).ConfigureAwait(false);
            return;
        }

        if (string.Equals(name, "seat.state", StringComparison.Ordinal))
        {
            // 返回前用权威投影同步一次：推送帧可能落后于本次响应（turn/handCount 差 1）。
            await RefreshViewAsync(ct).ConfigureAwait(false);
            var state = _state.Snapshot();
            await WriteToolResultAsync(output, id, Success(state), isError: false).ConfigureAwait(false);
            return;
        }

        if (string.Equals(name, "seat.board", StringComparison.Ordinal))
        {
            JsonNode board;
            try
            {
                board = await BuildBoardAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteToolResultAsync(
                    output, id, Failure("not-connected", $"读取战场摘要失败：{ex.Message}"), isError: true)
                    .ConfigureAwait(false);
                return;
            }

            await WriteToolResultAsync(output, id, Success(board), isError: false).ConfigureAwait(false);
            return;
        }

        if (string.Equals(name, "seat.segments", StringComparison.Ordinal))
        {
            long? since = null;
            if (arguments["sinceSequence"] is { } sinceNode)
            {
                try
                {
                    since = sinceNode.GetValue<long>();
                }
                catch (Exception)
                {
                    since = null;
                }
            }

            var segments = _state.ReadSegments(since);
            await WriteToolResultAsync(output, id, Success(segments), isError: false).ConfigureAwait(false);
            return;
        }

        try
        {
            var payload = await _transport.CallAsync(name, arguments, ct).ConfigureAwait(false);

            // 动作类调用结束后校正 pending：服务端提交成功后不发"待决已结束"通知，
            // 不校正会让 pendingSessionId 长期残留（agent 会误判还有待决请求）。
            _state.ApplyCallResult(name, payload);

            // R4：动作类调用在返回前用权威视图刷新一次——推送帧往往晚于本次响应，否则同一载荷里
            // `state.turn/activeSide/yourTurn` 会描述**动作前**的局面，与 `details` 冲突（两个真相）。
            // 与 seat.state / seat.board 分支同一手法；刷新失败已在 RefreshViewAsync 内隔离，不打断调用。
            if (SeatStateTracker.IsActionTool(name))
            {
                await RefreshViewAsync(ct).ConfigureAwait(false);
            }

            // 不能直接用 SerializeToNode：payload.Details 是已挂在父节点上的 JsonNode，
            // System.Text.Json 会把它**原样 attach** 到新节点并抛 "The node already has a parent"。
            // 走一轮字符串再解析，等价于强制深拷贝（也是唯一稳定的跨 JsonNode 边界做法）。
            var node = JsonNode.Parse(JsonSerializer.Serialize(payload, ProtocolJson.Options)) ?? new JsonObject();
            await WriteToolResultAsync(output, id, node, isError: !payload.Ok).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 打印完整堆栈：仅 Message 不足以定位（例如 JsonNode 父子关系这类结构性错误）。
            Console.Error.WriteLine($"[bridge] tools/call '{name}' 失败：{ex}");
            ExitCode = 0;
            _stop = true;
            await WriteToolResultAsync(output, id, Failure("not-connected", "与服务端的连接已断开。"), isError: true)
                .ConfigureAwait(false);
        }
    }

    /// <summary>用 <c>observe.view</c> 刷新桥内状态（失败不打断调用方）。</summary>
    private async Task RefreshViewAsync(CancellationToken ct)
    {
        try
        {
            var payload = await _transport.CallAsync("observe.view", new JsonObject(), ct).ConfigureAwait(false);
            if (payload.Ok && payload.Details is JsonObject details)
            {
                _state.ApplyView(details);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] 状态刷新失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 构造精简战场摘要（约 1–2 KB）。全量 <c>observe.view</c> 单次 3–5 万字符，
    /// agent 每回合消费一次会把上下文打爆；摘要只保留决策必需字段。
    /// </summary>
    private async Task<JsonNode> BuildBoardAsync(CancellationToken ct)
    {
        var payload = await _transport.CallAsync("observe.view", new JsonObject(), ct).ConfigureAwait(false);
        if (!payload.Ok || payload.Details is not JsonObject details)
        {
            throw new InvalidOperationException(payload.Message ?? "视图不可用。");
        }

        _state.ApplyView(details);

        var view = details["view"] as JsonObject ?? new JsonObject();
        var actions = details["actions"] as JsonObject ?? new JsonObject();
        var selfLine = view["selfLine"] as JsonArray ?? new JsonArray();
        var selfHq = view["selfHq"] as JsonObject;

        var occupiedSupport = new HashSet<int>();
        foreach (var node in selfLine)
        {
            if (node is JsonObject card && string.Equals(Str(card["zone"]), "support", StringComparison.Ordinal))
            {
                occupiedSupport.Add(Int(card["slotIndex"]));
            }
        }

        if (selfHq is not null)
        {
            occupiedSupport.Add(Int(selfHq["slotIndex"]));
        }

        var occupiedFront = new HashSet<int>();
        foreach (var node in selfLine)
        {
            if (node is JsonObject card && string.Equals(Str(card["zone"]), "frontline", StringComparison.Ordinal))
            {
                occupiedFront.Add(Int(card["slotIndex"]));
            }
        }

        var emptySupport = new JsonArray();
        for (var i = 0; i < Int(view["supportLineSlotCount"]); i++)
        {
            if (!occupiedSupport.Contains(i))
            {
                emptySupport.Add(i);
            }
        }

        var emptyFront = new JsonArray();
        for (var i = 0; i < Int(view["frontLineSlotCount"]); i++)
        {
            if (!occupiedFront.Contains(i))
            {
                emptyFront.Add(i);
            }
        }

        var pending = _state.Snapshot()["pendingSessionId"]?.DeepClone();

        return new JsonObject
        {
            ["turn"] = Int(view["turn"]),
            ["phase"] = Str(view["phase"]),
            ["activeSide"] = Str(view["activePlayerSide"]),
            ["yourTurn"] = string.Equals(Str(view["activePlayerSide"]), "self", StringComparison.Ordinal),
            ["kredits"] = new JsonObject
            {
                ["self"] = Int(view["selfKredits"]),
                ["max"] = Int(view["selfMaxKredits"]),
            },
            ["hq"] = new JsonObject
            {
                ["self"] = Int(selfHq?["health"]),
                ["enemy"] = Int((view["enemyHq"] as JsonObject)?["health"]),
            },
            // R5：手牌总数与已武装反制数分列（前者含已武装，后者与 observe.view 的 selfCounterCount 同口径）。
            ["handCount"] = Int(view["selfHandCount"]),
            ["armedCounterCount"] = Int(view["selfCounterCount"]),
            ["hand"] = CompactCards(view["selfHand"] as JsonArray, indexField: "handIndex"),
            ["selfLine"] = CompactCards(selfLine, indexField: "slot"),
            ["enemyLine"] = CompactCards(view["enemyLine"] as JsonArray, indexField: "slot"),
            ["emptySupportSlots"] = emptySupport,
            ["emptyFrontSlots"] = emptyFront,
            ["playable"] = actions["playableUids"]?.DeepClone() ?? new JsonArray(),
            ["attackPreviews"] = actions["attackPreviews"]?.DeepClone() ?? new JsonArray(),
            ["moves"] = actions["moves"]?.DeepClone() ?? new JsonArray(),
            ["pendingSessionId"] = pending,
        };
    }

    /// <summary>
    /// 卡列表精简（战场摘要用）。
    /// <para>
    /// <paramref name="indexField"/>：序号字段名——手牌用 <c>handIndex</c>（手牌数组内序号），
    /// 战线用 <c>slot</c>（槽位号）。两者是**不同域**：同名叫 <c>slot</c> 时 agent 会把手牌位置当槽位用
    /// （手牌第 2 张 ≠ 支援线 2 号槽），这类误用不会报错、只会安静地选错目标。
    /// </para>
    /// </summary>
    private static JsonArray CompactCards(JsonArray? cards, string indexField)
    {
        var result = new JsonArray();
        foreach (var node in cards ?? new JsonArray())
        {
            if (node is not JsonObject card)
            {
                continue;
            }

            result.Add(new JsonObject
            {
                ["uid"] = Str(card["uid"]),
                ["name"] = Str((card["definition"] as JsonObject)?["name"]),
                ["cost"] = Int(card["effectiveCost"]),
                ["atk"] = Int(card["effectiveAttack"]),
                ["hp"] = Int(card["health"]),
                [indexField] = Int(card["slotIndex"]),
                ["zone"] = Str(card["zone"]),
                // R5：反制「已武装 / 未武装」的区别——没有这个字段，agent 只能靠点数反推，
                // 而点数还受其他扣费影响（反推必然出错）。
                ["isCounterArmed"] = Bool(card["isCounterArmed"]),
            });
        }

        return result;
    }

    private static int Int(JsonNode? node)
    {
        if (node is null)
        {
            return 0;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool Bool(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Str(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception)
        {
            return node.ToJsonString().Trim('"');
        }
    }

    /// <summary>
    /// 工具结果外壳：<c>content[0].text</c> 是 <c>{"result":...,"state":...}</c> 的 JSON 文本。
    /// <para>每次调用都附精简 state（agent 决策依赖最新态；省 token 抵不上基于旧态决策的代价）。</para>
    /// </summary>
    private async Task WriteToolResultAsync(TextWriter output, JsonNode? id, JsonNode result, bool isError)
    {
        var envelope = new JsonObject
        {
            ["result"] = result.DeepClone(),
            ["state"] = _state.Snapshot(),
        };

        var message = new JsonObject
        {
            ["jsonrpc"] = ProtocolInfo.JsonRpcVersion,
            ["id"] = id?.DeepClone(),
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = envelope.ToJsonString(),
                    },
                },
                ["isError"] = isError,
            },
        };

        await WriteLineAsync(output, message).ConfigureAwait(false);
    }

    private static JsonNode Success(JsonNode payload) => new JsonObject
    {
        ["ok"] = true,
        ["code"] = "ok",
        ["message"] = "已读取。",
        ["details"] = payload.DeepClone(),
    };

    private static JsonNode Failure(string code, string message) => new JsonObject
    {
        ["ok"] = false,
        ["code"] = code,
        ["message"] = message,
    };

    private static async Task WriteResultAsync(TextWriter output, JsonNode? id, JsonNode result)
        => await WriteLineAsync(output, new JsonObject
        {
            ["jsonrpc"] = ProtocolInfo.JsonRpcVersion,
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        }).ConfigureAwait(false);

    private static async Task WriteErrorAsync(TextWriter output, JsonNode? id, int code, string message)
        => await WriteLineAsync(output, new JsonObject
        {
            ["jsonrpc"] = ProtocolInfo.JsonRpcVersion,
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }).ConfigureAwait(false);

    private static async Task WriteLineAsync(TextWriter output, JsonObject message)
    {
        await output.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static JsonObject BuildSchema(string json) => (JsonObject)JsonNode.Parse(json)!;
}
