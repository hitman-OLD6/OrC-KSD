using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Mcp.Bridge;

/// <summary>
/// 可拉取状态（无头沙盒 S5）：把服务端推送转成"当前态快照 ＋ 段缓冲"。
/// <para>
/// **state 是当前态快照，不是增量流**：任何入站帧 <see cref="StateVersion"/> ＋1；只保留最后一条 view。
/// 段是 **best-effort**（定长 200，超出丢最旧并计数 <c>dropped</c>）——完整性的唯一承诺在服务端
/// <c>events.jsonl</c>（S4）。
/// </para>
/// </summary>
public sealed class SeatStateTracker
{
    /// <summary>段缓冲容量（与 <c>SegmentCache</c> 同量级）。</summary>
    public const int SegmentCapacity = 200;

    private readonly object _sync = new();
    private readonly Queue<JsonNode> _segments = new();
    private JsonObject? _lastView;
    private string? _pendingSessionId;
    private long _stateVersion;
    private long _dropped;
    private long _lastSequence = -1;

    /// <summary>状态版本（任何入站帧 ＋1；跨过多个值＝期间有多次变化）。</summary>
    public long StateVersion
    {
        get
        {
            lock (_sync)
            {
                return _stateVersion;
            }
        }
    }

    /// <summary>累计丢弃的段数（不把"序号跳跃"留给 agent 猜）。</summary>
    public long Dropped
    {
        get
        {
            lock (_sync)
            {
                return _dropped;
            }
        }
    }

    /// <summary>缓冲中的段数。</summary>
    public int Buffered
    {
        get
        {
            lock (_sync)
            {
                return _segments.Count;
            }
        }
    }

    /// <summary>入站帧处理（view/segment/target/match 一律推进版本号）。</summary>
    public void OnNotification(RpcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        lock (_sync)
        {
            _stateVersion++;

            switch (notification.Method)
            {
                case ProtocolMethods.NotifyView:
                    if (notification.Params is JsonObject view)
                    {
                        // 只保留最后一条 view：中间变化由 stateVersion 与 dropped 表达。
                        _lastView = (JsonObject)view.DeepClone();
                    }

                    break;

                case ProtocolMethods.NotifySegment:
                    if (notification.Params?["segments"] is JsonArray segments)
                    {
                        foreach (var segment in segments)
                        {
                            if (segment is null)
                            {
                                continue;
                            }

                            _segments.Enqueue(segment.DeepClone());
                            if (_segments.Count > SegmentCapacity)
                            {
                                _segments.Dequeue();
                                _dropped++;
                            }

                            if (segment["sequence"] is { } sequence
                                && long.TryParse(sequence.ToJsonString(), out var value))
                            {
                                _lastSequence = value;
                            }
                        }
                    }

                    break;

                case ProtocolMethods.NotifyTargetRequest:
                    _pendingSessionId = notification.Params?["sessionId"]?.GetValue<string>();
                    break;

                case ProtocolMethods.NotifyTargetExpired:
                {
                    var expired = notification.Params?["sessionId"]?.GetValue<string>();
                    if (expired is null || string.Equals(expired, _pendingSessionId, StringComparison.Ordinal))
                    {
                        _pendingSessionId = null;
                    }

                    break;
                }

                case ProtocolMethods.NotifyMatchStarted:
                case ProtocolMethods.NotifyMatchEnded:
                    _pendingSessionId = null;
                    break;
            }
        }
    }

    /// <summary>
    /// 用一次工具调用的结果校正 pending（仅动作类调用）。
    /// <para>
    /// 为什么需要：服务端只在 <c>target.expired</c> / <c>match.started</c> 发清理信号，
    /// **提交成功后不发任何"待决已结束"通知**——不校正的话 <c>pendingSessionId</c> 会一直残留，
    /// agent 会误以为还有待决请求。规则：动作类调用结束后，以返回载荷里的 <c>details.pending</c>
    /// 为准（有则更新，无则清空）。
    /// </para>
    /// </summary>
    public void ApplyCallResult(string tool, ToolResultPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!IsActionTool(tool))
        {
            return;
        }

        string? pending = null;
        try
        {
            pending = payload.Details?["pending"]?["sessionId"]?.GetValue<string>();
        }
        catch (Exception)
        {
            // 载荷形状异常时按"无待决"处理（宁可少报，不可误报）。
            pending = null;
        }

        lock (_sync)
        {
            if (!string.Equals(_pendingSessionId, pending, StringComparison.Ordinal))
            {
                _pendingSessionId = pending;
                _stateVersion++;
            }
        }
    }

    /// <summary>
    /// 用权威投影（<c>observe.view</c> 的 <c>details</c>）刷新视图快照。
    /// <para>
    /// 推送帧有延迟：<c>tools/call</c> 的响应往往先于 <c>notify/view</c> 到达，导致 state 落后一帧
    /// （turn/handCount 各差 1）。<c>seat.state</c> 与 <c>seat.board</c> 在返回前用本方法同步一次，
    /// 保证读到的是当前权威态。
    /// </para>
    /// </summary>
    public void ApplyView(JsonObject details)
    {
        ArgumentNullException.ThrowIfNull(details);

        if (details["view"] is not JsonObject view)
        {
            return;
        }

        lock (_sync)
        {
            _lastView = (JsonObject)view.DeepClone();
            _stateVersion++;
        }
    }

    /// <summary>
    /// 是否为「动作类」工具（会推进引擎、产生待决请求或改变局面的调用）。
    /// <para>
    /// 公开给 <see cref="BridgeServer"/> 复用同一前缀集合：待决校正（<see cref="ApplyCallResult"/>）与
    /// 视图刷新（R4）必须按同一分类走，两处各写一份前缀会让「哪种调用算动作」出现两个真相。
    /// </para>
    /// </summary>
    public static bool IsActionTool(string tool)
        => tool.StartsWith("play.", StringComparison.Ordinal)
            || tool.StartsWith("command.", StringComparison.Ordinal)
            || tool.StartsWith("match.", StringComparison.Ordinal)
            || tool.StartsWith("mulligan.", StringComparison.Ordinal)
            || tool.StartsWith("target.", StringComparison.Ordinal);

    /// <summary>精简状态（固定 &lt;15 个字段；全量视图仍由服务端 <c>observe.view</c> 提供）。</summary>
    public JsonObject Snapshot()
    {
        lock (_sync)
        {
            var phase = _lastView?["phase"]?.GetValue<string>();
            var activeSide = _lastView?["activePlayerSide"]?.GetValue<string>();
            var yourTurn = string.Equals(activeSide, "self", StringComparison.Ordinal);

            var state = new JsonObject
            {
                ["stateVersion"] = _stateVersion,
                ["phase"] = phase,
                ["turn"] = ReadInt(_lastView?["turn"]),
                ["activeSide"] = activeSide,
                ["yourTurn"] = yourTurn,
                ["handCount"] = ReadInt(_lastView?["selfHandCount"]),
                ["enemyHandCount"] = ReadInt(_lastView?["enemyHandCount"]),
                ["segments"] = new JsonObject
                {
                    ["buffered"] = _segments.Count,
                    ["dropped"] = _dropped,
                    ["lastSequence"] = _lastSequence,
                },
            };

            if (_pendingSessionId is not null)
            {
                state["pendingSessionId"] = _pendingSessionId;
            }

            return state;
        }
    }

    /// <summary>
    /// 读段缓冲（只读，最多 200 段）。<paramref name="sinceSequence"/> 之后的段优先；
    /// 若请求的起点早于缓冲最旧段（或已有丢弃）则 <c>truncated:true</c>。
    /// </summary>
    public JsonObject ReadSegments(long? sinceSequence)
    {
        lock (_sync)
        {
            var selected = new JsonArray();
            var truncated = false;

            foreach (var segment in _segments)
            {
                var sequence = ReadLong(segment?["sequence"]);
                if (sinceSequence is { } since && sequence <= since)
                {
                    continue;
                }

                selected.Add(segment!.DeepClone());
            }

            if (sinceSequence is { } requested && _segments.Count > 0)
            {
                var oldest = ReadLong(_segments.Peek()?["sequence"]);
                truncated = oldest > requested + 1;
            }

            return new JsonObject
            {
                ["segments"] = selected,
                ["buffered"] = _segments.Count,
                ["dropped"] = _dropped,
                ["lastSequence"] = _lastSequence,
                ["truncated"] = truncated,
            };
        }
    }

    private static int ReadInt(JsonNode? node)
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

    private static long ReadLong(JsonNode? node)
    {
        if (node is null)
        {
            return -1;
        }

        try
        {
            return node.GetValue<long>();
        }
        catch (Exception)
        {
            return long.TryParse(node.ToJsonString(), out var value) ? value : -1;
        }
    }
}
