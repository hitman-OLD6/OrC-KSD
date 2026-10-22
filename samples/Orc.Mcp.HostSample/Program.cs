// =============================================================================
// Orc.Mcp.HostSample —— **UI 宿主集成的可运行参考宿主**（控制台伪 UI，无 Godot 依赖）
//
// 用途：把 UI 侧接入服务层所需的每一步接线都在一个可运行程序里演示出来——
//   ① 会话与门控：唯一 McpService 实例（唯一桥 + 唯一门禁 + 唯一段取用点）
//   ② 对局生命周期：MatchChanged 事件 → 建立/销毁投影 provider（UI 自建 provider）
//   ③ 每帧投影：provider.Project(match, viewer) → 渲染数据（本示例打印摘要）
//   ④ 待决选择请求：SubscribeTargetingRequests → 模拟"人在场"认领并点选
//   ⑤ 段消费：声明 Host 为段取用者 → 宿主取段 → 喂给服务层 → 从服务层读回
//   ⑥ 驱动面：强类型动作 API（EndTurnAsync）——UI 与 agent 共用同一门禁
//
// 运行：
//   dotnet run --project samples/Orc.Mcp.HostSample
// =============================================================================

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Hosting;
using Orc.Mcp.Match;

namespace Orc.Mcp.HostSample;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ① 宿主侧沙盒根（UI 形态下通常放在宿主工作目录；此处用临时目录以便反复运行）。
        var sandboxRoot = Path.Combine(Path.GetTempPath(), "orc-mcp-host-sample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);

        var service = new McpService(new McpServiceOptions
        {
            Mode = McpServiceMode.Authoring,
            SandboxRoot = sandboxRoot,
            Seed = 20261016,
        });

        Console.WriteLine($"== 参考宿主启动：sandbox={sandboxRoot} ==");
        Console.WriteLine($"工具数：{service.Tools.Count}；模式：{service.Options.Mode}");

        // ② 对局生命周期 → 投影 provider（UI 自建；美术路径与 matchId 只有 UI 侧知道）
        ProjectionHandle? projection = null;
        service.MatchChanged += (_, args) =>
        {
            if (args.Change == MatchLifecycleChange.Created && args.Match is { } match)
            {
                projection = new ProjectionHandle(match.UiProjections.CreateProvider(
                    artLookup: null,
                    matchId: service.Options.MatchId,
                    definitionLookup: null));
                Console.WriteLine($"[UI] 对局已创建 → 投影 provider 就绪（hasProvider={match.UiProjections.HasProvider}）");
            }
            else
            {
                projection = null;
                Console.WriteLine("[UI] 对局已销毁 → 停止投影");
            }
        };

        // ④ 待决选择请求 → 模拟"人在场"：回调里只**渲染 + 认领**（真实 UI 在此绘制候选并等玩家点击），
        //    实际提交发生在下一帧（不在桥的回调线程上同步阻塞）。
        PendingSelection? pendingForPlayer = null;
        using var targetingSubscription = service.SubscribeTargetingRequests(pending =>
        {
            Console.WriteLine($"[UI] 收到待决选择：selector={pending.SelectorName} min={pending.Min} max={pending.Max} "
                + $"候选={pending.Candidates.Count}，sessionId={pending.SessionId[..8]}…");

            if (!service.TryClaimTargeting(pending.SessionId, TargeterBridgeAdapter.ClaimantHost, out var reason))
            {
                Console.WriteLine($"[UI] 认领失败（可能已被 agent 应答）：{reason}");
                return;
            }

            pendingForPlayer = pending; // 交给帧循环里的"玩家点击"
        });

        // ⑤ 段消费：宿主担任取用者（UI 形态）——宿主帧循环取段并喂给服务层
        service.SetSegmentConsumer(SegmentConsumer.Host);
        Console.WriteLine($"[UI] 段取用者＝{service.SegmentConsumer}（服务层不再自行取段）");

        // ---------- 会话流程 ----------

        await CallAsync(service, "service.info");
        var created = await service.CreateSandboxMatchAsync();
        Console.WriteLine($"[host] 进程内建沙盒局：ok={created.Ok} code={created.Code}");

        // 写一张卡 → 校验 → 逐卡注册（agent 通常走 MCP 工具；此处用同一 InvokeAsync 入口演示）
        var cardJson = """
        {
          "schemaVersion": 1,
          "id": "host.demo.rifleman",
          "name": "宿主演示步枪兵",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 0, "attack": 2, "defense": 3 },
            { "component": "tagData", "rarity": "standard" }
          ]
        }
        """;

        await CallAsync(service, "card.write", new JsonObject
        {
            ["path"] = "pack/cards/host.demo.rifleman.card.json",
            ["json"] = cardJson,
        });
        await CallAsync(service, "asset.validate");
        await CallAsync(service, "card.load");

        // 造一张卡到手牌 → 用强类型动作面预打出（触发选位 → 上面订阅的"人"会应答）
        var spawned = await CallAsync(service, "sandbox.spawn", new JsonObject
        {
            ["definitionId"] = "host.demo.rifleman",
            ["owner"] = 0,
            ["zone"] = "hand",
        });

        var cardRef = spawned?["ref"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(cardRef))
        {
            Console.WriteLine("[agent] 预打出（将进入部署选位）…");
            var prePlay = await service.PlayUnitPreAsync(cardRef);
            Console.WriteLine($"[agent] 预打出结局：status={prePlay.Status} code={prePlay.FailureCode ?? "ok"}");
        }

        // ③ 每帧投影（本示例手动跑 3 帧）；帧循环同时处理"玩家点击"
        for (var frame = 1; frame <= 3; frame++)
        {
            // 玩家点击发生在帧循环里（不在桥的回调线程上）
            if (pendingForPlayer is { } pending)
            {
                pendingForPlayer = null;
                var picks = pending.Min > 0 && pending.Candidates.Count > 0 ? new[] { 1 } : Array.Empty<int>();
                var submitted = service.TrySubmitTargeting(
                    pending.SessionId, picks, false, TargeterBridgeAdapter.ClaimantHost, out var reason);

                Console.WriteLine(submitted
                    ? $"[UI] 玩家点选 → 提交成功（picks=[{string.Join(',', picks)}]）"
                    : $"[UI] 玩家点选 → 提交失败：{reason}");
            }

            PrettyPrintFrame(frame, service, projection);
            PumpSegments(service);
            await Task.Delay(30);
        }

        // ⑥ 强类型动作面（UI 与 agent 共用同一门禁）
        var endTurn = await service.EndTurnAsync();
        Console.WriteLine($"[UI] EndTurnAsync → status={endTurn.Status}");

        // 收尾：确认无待决后销毁对局
        if (service.Executor.IsBusy && service.Bridge.CurrentPending is { } stillPending)
        {
            service.TrySubmitTargeting(
                stillPending.SessionId, Array.Empty<int>(), cancel: true, TargeterBridgeAdapter.ClaimantHost, out _);
        }

        var disposed = service.DisposeSandboxMatch();
        Console.WriteLine($"[host] 进程内销毁沙盒局：ok={disposed.Ok} code={disposed.Code}");

        try
        {
            Directory.Delete(sandboxRoot, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响示例结论。
        }

        Console.WriteLine("== 参考宿主演示结束 ==");
        return 0;
    }

    /// <summary>每帧投影（真实 UI 在此把 UiMatchView 交给渲染层）。</summary>
    private static void PrettyPrintFrame(int frame, McpService service, ProjectionHandle? projection)
    {
        if (projection?.Provider is null || service.ActiveMatch is not { } match)
        {
            Console.WriteLine($"[帧 {frame}] 无活跃对局或投影不可用");
            return;
        }

        try
        {
            var view = projection.Provider.Project(match, match.Players[ 0]);
            Console.WriteLine(
                $"[帧 {frame}] 投影：回合={view.Turn} 相位={view.Phase} 我方手牌={view.SelfHandCount} 卡组={view.SelfDeckCount} "
                + $"敌方手牌={view.EnemyHandCount?.ToString() ?? "?"} 指挥点={view.SelfKredits}/{view.SelfMaxKredits}");
        }
        catch (Exception ex)
        {
            // UI 侧投影隔离口径：异常保留上一帧、不炸对局。
            Console.WriteLine($"[帧 {frame}] 投影异常（按隔离处理，保留上一帧）：{ex.GetType().Name}");
        }
    }

    /// <summary>宿主帧循环取段并喂给服务层（UI 形态），随后从服务层缓存读回（agent 侧 observe.segments 同源）。</summary>
    private static void PumpSegments(McpService service)
    {
        if (service.ActiveMatch is not { } match)
        {
            return;
        }

        var segments = match.Engine.TakeSegments();
        if (segments.Count == 0)
        {
            return;
        }

        service.IngestSegments(segments);
        var cached = service.PullSegments(take: 1);
        Console.WriteLine($"[UI] 取段 {segments.Count} 条 → 喂给服务层（缓存共 {service.Segments.Count} 条；最近序列 {cached.FirstOrDefault()?.Sequence}）");
    }

    private static async Task<JsonNode?> CallAsync(McpService service, string tool, JsonNode? arguments = null)
    {
        using var document = JsonDocument.Parse(arguments?.ToJsonString(JsonOptions) ?? "{}");
        var call = new McpToolCall(tool, document.RootElement.Clone());
        var result = await service.InvokeAsync(call);

        Console.WriteLine($"[tool] {tool} → ok={result.Ok} code={result.Code} {result.Message}");

        return result.EngineResult ?? result.Details;
    }

    /// <summary>投影句柄（宿主持有；对局销毁时置空——不读已销毁对局）。</summary>
    private sealed record ProjectionHandle(Orc.Game.Ui.IUiProjectionProvider? Provider);
}
