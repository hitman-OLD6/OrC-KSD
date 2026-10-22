using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orc.Manager;

/// <summary>
/// 管理器的 **stdio MCP 前端**：把 MCP 的 <c>tools/call</c> 转成对常驻管理器的回环调用。
/// <para>
/// 为什么要有这一层：宿主（WorkBuddy 等）的 MCP 配置是 <c>command + args</c>（stdio）形态，
/// 而管理器必须常驻（生命周期长于宿主、可被多个 agent 共用）。前端是**无状态薄适配**——
/// 它不持有实例，也不做任何判断；管理器没起来时，它的每次调用都会如实失败。
/// </para>
/// <para>
/// 与服务器侧的 <c>Orc.Mcp.Bridge</c> 同构：两者都是"stdio ↔ 回环 TCP"的转接，
/// 区别只是对端从"一台服务器实例"变成"管理器"。
/// </para>
/// </summary>
internal sealed class ManagerMcpFront
{
    private const string ProtocolVersion = "2024-11-05";

    private readonly ManagerClient _client;

    // 工具表是否需要在响应之后通知宿主刷新（MCP 的 tools/list_changed）。
    // 用 int 而非 bool：Interlocked.Exchange 需要引用类型或 32/64 位整数。
    private int _toolsDirty;

    public ManagerMcpFront(ManagerClient client) => _client = client;

    /// <summary>跑 stdio 主循环，直到输入关闭（返回进程退出码）。</summary>
    public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject
                    ?? throw new JsonException("请求必须是 JSON 对象。");
            }
            catch (JsonException)
            {
                continue; // 非法行：MCP 客户端不会发，忽略即可（不污染 stdout）。
            }

            var response = await HandleAsync(request, ct).ConfigureAwait(false);
            if (response is null)
            {
                continue; // 通知：不响应。
            }

            await output.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);

            // 环境变了（实例被拉起/销毁、代持连接建立/断开）就提示宿主重拉工具表。
            // 注意：**工具表本身是恒定的**（见 ToolList），这条通知只是让支持它的宿主刷新，
            // 不支持 list_changed 的宿主也照常能用——这是"不重启宿主"的底线保障。
            if (Interlocked.Exchange(ref _toolsDirty, 0) == 1)
            {
                await output.WriteLineAsync(
                    """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""").ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private async Task<JsonObject?> HandleAsync(JsonObject request, CancellationToken ct)
    {
        var method = request["method"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        // 通知（无 id）：一律不响应。
        if (!request.ContainsKey("id"))
        {
            return null;
        }

        var id = request["id"]!.DeepClone();
        var parameters = request["params"] as JsonObject;

        try
        {
            return method switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    // listChanged=true：宿主可监听 tools/list_changed 主动刷新；
                    // 即使宿主不监听，工具表也是恒定的（见 ToolList），不会因此不可用。
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "orc-manager",
                        ["version"] = "1.0.0",
                    },
                }),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = ToolList() }),
                "tools/call" => Result(id, await CallToolAsync(parameters, ct).ConfigureAwait(false)),
                _ => RpcError(id, -32601, $"未实现的方法 '{method}'。"),
            };
        }
        catch (Exception ex)
        {
            return RpcError(id, -32603, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolError("缺少工具名。");
        }

        // MCP 工具名 → 管理器方法：同一套语义，只换调用形态（工具名带点号是 MCP 客户端普遍接受的形式）。
        var method = name switch
        {
            "manager.ping" => "manager.ping",
            "manager.list" => "manager.list",
            "manager.spawn" => "manager.spawn",
            "manager.kill" => "manager.kill",
            "manager.reload" => "manager.reload",
            "manager.admin" => "manager.admin",
            "manager.connect" => "manager.connect",
            "manager.action" => "manager.action",
            "manager.sessions" => "manager.sessions",
            "manager.disconnect" => "manager.disconnect",
            _ => null,
        };

        if (method is null)
        {
            return ToolError($"未知工具 '{name}'。");
        }

        ManagerReply reply;
        try
        {
            reply = await _client.InvokeAsync(method, arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            // 管理器不在线：这是最常见的配置错误（忘了先启动 serve），给出可操作的提示。
            return ToolError(
                $"无法连接管理器（{ex.GetType().Name}: {ex.Message}）。请先启动 `orc-manager serve --port <端口>`，"
                + "并确认前端传入的 --connect 与之一致。");
        }

        var payload = new JsonObject
        {
            ["ok"] = reply.Ok,
            ["code"] = reply.Code,
            ["message"] = reply.Message,
            ["payload"] = reply.Payload?.DeepClone(),
        };

        // 这几类操作会改变"当前可做的事"：新增/销毁实例、建立/断开代持连接。
        if (reply.Ok && name is "manager.spawn" or "manager.kill" or "manager.reload"
            or "manager.connect" or "manager.disconnect")
        {
            Interlocked.Exchange(ref _toolsDirty, 1);
        }

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(JsonOptions),
                },
            },
            ["isError"] = !reply.Ok,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = message },
        },
        ["isError"] = true,
    };

    private static JsonObject Result(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject RpcError(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonArray ToolList() =>
    [
        Tool(
            "manager.ping",
            "探活：确认管理器在线（返回协议版本）。",
            """{ "type": "object", "properties": {}, "additionalProperties": false }"""),
        Tool(
            "manager.list",
            "列出管理器托管的所有服务器实例（含实例标识、端口、roomId、运行目录与是否存活）。"
            + "拿到 port 后，玩家/调试面可用 Orc.Mcp.Bridge 连该端口接入对局。",
            """{ "type": "object", "properties": {}, "additionalProperties": false }"""),
        Tool(
            "manager.spawn",
            "启动一台服务器实例（独立子进程）。seat 形态在启动时声明：ai 由服务端托管、agent 等外部接入。"
            + "返回实例标识与业务端口。",
            """
            {
              "type": "object",
              "properties": {
                "instanceId": { "type": "string", "description": "实例标识（管理器内唯一）" },
                "packDirectories": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "卡包目录（绝对路径）"
                },
                "seats": {
                  "type": "array",
                  "description": "两个座位的形态（缺省两个 agent 座）",
                  "items": {
                    "type": "object",
                    "properties": {
                      "kind": { "type": "string", "enum": ["ai", "agent"] },
                      "displayName": { "type": "string" },
                      "packages": {
                        "type": "array",
                        "description": "该座位的卡包引用（id/version/guid）；AI 座缺省为空集",
                        "items": {
                          "type": "object",
                          "properties": {
                            "id": { "type": "string" },
                            "version": { "type": "string" },
                            "guid": { "type": "string" }
                          },
                          "required": ["id", "version", "guid"]
                        }
                      },
                      "deck": { "type": "array", "items": { "type": "string" } }
                    }
                  }
                },
                "mode": { "type": "string", "enum": ["play", "sandbox"], "description": "play＝生产面；sandbox＝额外暴露调试工具" },
                "allowDebugWrites": { "type": "boolean", "description": "仅 sandbox 有效：注册写类调试工具" },
                "sandboxRoot": { "type": "string", "description": "沙盒写根（缺省＝运行目录下 sandbox）" },
                "port": { "type": "integer", "description": "业务端口（0＝系统分配）" },
                "runDir": { "type": "string", "description": "运行目录（日志/沙盒落点）" },
                "seed": { "type": "integer" },
                "deckRules": { "type": "string", "enum": ["strict", "lenient", "none"] },
                "disconnectGraceSeconds": { "type": "integer" },
                "label": { "type": "string" }
              },
              "required": ["instanceId"]
            }
            """),
        Tool(
            "manager.kill",
            "销毁一台服务器实例（进程树回收、端口可复用）。",
            """
            {
              "type": "object",
              "properties": { "instanceId": { "type": "string" } },
              "required": ["instanceId"],
              "additionalProperties": false
            }
            """),
        Tool(
            "manager.reload",
            "热更新：销毁旧子进程、用当前 dll 起新子进程（管理器不重启）。",
            """
            {
              "type": "object",
              "properties": { "instanceId": { "type": "string" } },
              "required": ["instanceId"],
              "additionalProperties": false
            }
            """),
        Tool(
            "manager.admin",
            "把管理面操作透传给指定实例（管理员动作）。常用 op："
            + "`connections.list`（看待许可/在座连接）、`conn.admit`（批准连接，参数 connectionId）、"
            + "`conn.reject`、`conn.revoke`、`seat.bind`、`seat.unbind`、`match.start`（开局）、"
            + "`room.abort`（无胜者终止）、`sandbox.reset`、`instance.info`。",
            """
            {
              "type": "object",
              "properties": {
                "instanceId": { "type": "string" },
                "op": { "type": "string", "description": "服务器管理面操作名" },
                "args": { "type": "object", "description": "该操作的参数对象" }
              },
              "required": ["instanceId", "op"],
              "additionalProperties": false
            }
            """),
        Tool(
            "manager.connect",
            "以某个角色接入一台实例，返回 sessionId ＋ **该连接可用的工具清单**。"
            + "seat 留空＝调试面（sandbox 实例才能造卡/写卡）；seat=0 或 1＝玩家，会自动兼任 viewer"
            + "（只申请 controller 的连接收不到任何输出，连自己的手牌都读不到），玩家座必须给 deck。"
            + "拿到 sessionId 后用 manager.action 调工具。",
            """
            {
              "type": "object",
              "properties": {
                "instanceId": { "type": "string" },
                "seat": { "type": "integer", "description": "0 或 1；留空＝调试面" },
                "roles": {
                  "type": "array",
                  "items": { "type": "string", "enum": ["controller", "viewer", "debug"] },
                  "description": "缺省：给了 seat＝[controller,viewer]，没给＝[debug]"
                },
                "deck": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "卡组（卡 id 列表，通常 39 张）；玩家座必填"
                },
                "packages": {
                  "type": "array",
                  "description": "卡包引用（id/version/guid）",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" },
                      "version": { "type": "string" },
                      "guid": { "type": "string" }
                    },
                    "required": ["id", "version", "guid"]
                  }
                },
                "displayName": { "type": "string" }
              },
              "required": ["instanceId"]
            }
            """),
        Tool(
            "manager.action",
            "在已建立的连接上调用实例工具（等价于该连接的 tools/call）。"
            + "tool 用 manager.connect 返回的 tools[].name，例如 observe.view / match.end_turn / "
            + "play.unit_pre / card.write / sandbox.spawn。",
            """
            {
              "type": "object",
              "properties": {
                "sessionId": { "type": "string" },
                "tool": { "type": "string" },
                "arguments": { "type": "object" }
              },
              "required": ["sessionId", "tool"],
              "additionalProperties": false
            }
            """),
        Tool(
            "manager.sessions",
            "列出管理器代持的连接（sessionId / 实例 / 座位 / 角色 / 是否仍连着）。",
            """{ "type": "object", "properties": {}, "additionalProperties": false }"""),
        Tool(
            "manager.disconnect",
            "断开一条代持连接并回收。",
            """
            {
              "type": "object",
              "properties": { "sessionId": { "type": "string" } },
              "required": ["sessionId"],
              "additionalProperties": false
            }
            """),
    ];

    private static JsonObject Tool(string name, string description, string inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = JsonNode.Parse(inputSchema),
    };
}
