// =============================================================================
// Orc.Mcp.Stdio —— OrC 引擎 MCP 服务层的**最小 stdio 适配示例**（协议无关服务层之宿主）
//
// 用法（一条命令即可启动）：
//   dotnet run --project samples/Orc.Mcp.Stdio -- --mode authoring
//
// 支持的 JSON-RPC 方法（MCP 最小可用集；本示例刻意只实现 tools 能力）：
//   initialize / notifications/initialized / tools/list / tools/call / ping
//
// 设计边界：
//   · 服务层（Orc.Mcp）零 MCP SDK 依赖；本工程手写最小 JSON-RPC 分发（无第三方依赖）。
//   · 业务失败走结果内嵌（内容里的 ok=false / code），**不置 isError**；
//     仅工具自身故障（code=internal）置 isError=true（与 R5 决策一致）。
//   · stdio 使用换行分隔 JSON（NDJSON）；stdout 只写协议帧，日志一律走 stderr。
// =============================================================================

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Mcp;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Stdio;

internal static class Program
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "orc-mcp";
    private const string ServerVersion = "0.1.0";

    /// <summary>JSON 写出选项：不转义非 ASCII（中文按原文输出；JSON 仍完全合法、体积更小）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var options = ParseOptions(args);
        using var service = new ServiceHolder(options);

        Log($"OrC MCP 服务已启动：mode={options.Mode}、sandbox={service.Service.Sandbox.Guard.Root}、tools={service.Service.Tools.Count}");

        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        await using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

        while (true)
        {
            var line = await stdin.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                Log("stdin 关闭，退出。");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // 容错：某些宿主/工具链会在首行带上 UTF-8 BOM（\uFEFF）——剥离后仍按 JSON 处理。
            var payload = line.TrimStart('\uFEFF', ' ', '\t');

            string? response = null;
            try
            {
                response = await HandleAsync(service.Service, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"处理失败：{ex.GetType().Name}: {ex.Message}");
                response = ErrorResponse(null, -32603, $"内部错误：{ex.Message}");
            }

            if (response is not null)
            {
                await stdout.WriteLineAsync(response).ConfigureAwait(false);
                await stdout.FlushAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> HandleAsync(McpService service, string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        var id = root.TryGetProperty("id", out var idElement) ? idElement : default;
        var hasId = id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString() ?? string.Empty
            : string.Empty;

        switch (method)
        {
            case "initialize":
                return ResultResponse(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion,
                    },
                    ["instructions"] = "OrC 卡牌引擎（沙盒/编辑器模式）：先 service.info 读限制清单；本进程只做"
                        + "资产层与诊断（card.write/card.load/observe.*/target.*）。建局/毁局属于管理面"
                        + "（match.start / room.abort），沙盒包脚手架由服务器 boot 段自动生成。",
                });

            case "notifications/initialized":
            case "notifications/cancelled":
                return null; // 通知：无响应

            case "ping":
                return hasId ? ResultResponse(id, new JsonObject()) : null;

            case "tools/list":
                return ResultResponse(id, ToolsList(service));

            case "tools/call":
            {
                if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                {
                    return ErrorResponse(id, -32602, "tools/call 缺少 params。");
                }

                var name = parameters.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;

                var arguments = parameters.TryGetProperty("arguments", out var argsElement)
                    && argsElement.ValueKind == JsonValueKind.Object
                        ? argsElement
                        : default;

                var result = await service.InvokeAsync(name, arguments).ConfigureAwait(false);
                return ResultResponse(id, ToolCallResult(result));
            }

            default:
                return hasId ? ErrorResponse(id, -32601, $"不支持的方法：{method}") : null;
        }
    }

    private static JsonObject ToolsList(McpService service)
    {
        var tools = new JsonArray();
        foreach (var descriptor in service.Tools)
        {
            JsonNode? schema;
            try
            {
                schema = JsonNode.Parse(descriptor.InputSchema);
            }
            catch (JsonException)
            {
                schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            }

            tools.Add(new JsonObject
            {
                ["name"] = descriptor.Name,
                ["description"] = descriptor.Description,
                ["inputSchema"] = schema,
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private static JsonObject ToolCallResult(McpToolResult result)
    {
        var payload = new JsonObject
        {
            ["ok"] = result.Ok,
            ["code"] = result.Code,
            ["message"] = result.Message,
        };

        if (result.Details is not null)
        {
            payload["details"] = result.Details.DeepClone();
        }

        if (result.EngineResult is not null)
        {
            payload["engineResult"] = result.EngineResult.DeepClone();
        }

        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = payload.ToJsonString(JsonOptions),
            },
        };

        return new JsonObject
        {
            ["content"] = content,
            // R5：仅"工具自身故障"置 isError；业务失败（ok=false）保持 false。
            ["isError"] = !result.Ok && result.Code == McpCodes.Internal,
        };
    }

    private static string ResultResponse(JsonElement id, JsonNode result)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = IdNode(id),
            ["result"] = result,
        }.ToJsonString(JsonOptions);

    private static string ErrorResponse(JsonElement? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is { } value ? IdNode(value) : null,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }.ToJsonString(JsonOptions);

    private static JsonNode? IdNode(JsonElement id)
        => id.ValueKind switch
        {
            JsonValueKind.Number => JsonValue.Create(id.GetInt64()),
            JsonValueKind.String => JsonValue.Create(id.GetString()),
            _ => null,
        };

    private static McpServiceOptions ParseOptions(string[] args)
    {
        var mode = McpServiceMode.Authoring;
        var sandbox = Path.Combine(Directory.GetCurrentDirectory(), ".orc-sandbox");
        string? packsRoot = null;
        var promote = false;
        var csx = false;
        int? seed = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode" when i + 1 < args.Length:
                    mode = string.Equals(args[++i], "player", StringComparison.OrdinalIgnoreCase)
                        ? McpServiceMode.Player
                        : McpServiceMode.Authoring;
                    break;

                case "--sandbox" when i + 1 < args.Length:
                    sandbox = args[++i];
                    break;

                case "--packs" when i + 1 < args.Length:
                    packsRoot = args[++i];
                    break;

                case "--promote":
                    promote = true;
                    break;

                case "--csx":
                    csx = true;
                    break;

                case "--seed" when i + 1 < args.Length:
                    seed = int.TryParse(args[++i], out var parsed) ? parsed : null;
                    break;
            }
        }

        return new McpServiceOptions
        {
            Mode = mode,
            SandboxRoot = sandbox,
            PacksRoot = packsRoot,
            PromoteEnabled = promote,
            CsxEnabled = csx,
            Seed = seed,
        };
    }

    private static void Log(string message)
        => Console.Error.WriteLine($"[orc-mcp] {message}");

    /// <summary>服务持有者（Options 已构建；便于 using 释放）。</summary>
    private sealed class ServiceHolder : IDisposable
    {
        public ServiceHolder(McpServiceOptions options) => Service = new McpService(options);

        public McpService Service { get; }

        public void Dispose()
        {
            // 服务层无外部非托管资源；对局随进程退出释放。
        }
    }
}
