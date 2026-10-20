using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Core;
using Orc.Game.Targeting;
using Orc.Protocol;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S6 跨玩家选靶：选靶归属座位 1 时，只有座位 1 的会话收到 <c>notify/target.request</c>。
/// <para>
/// 示例卡包没有"让对手选择"的效果，故直接经 <c>Match.TargeterManager</c> 发起一次归属敌方的选靶，
/// 只验证路由与推送，不验证效果语义。
/// </para>
/// </summary>
public class CrossSeatTargetingTests
{
    [Fact]
    public async Task TargetingOwnedByEnemy_IsPushedOnlyToEnemySeat()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-cross-seat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var host = await ServerBootstrap.StartAsync(new ServerOptions
            {
                PackDirectories = [SamplePackDirectory()],
                SandboxRoot = root,
                Port = 0,
                EnforceDeckRules = false,
            });

            await using var hostClient = await ConnectAsync(host.Port);
            await using var guestClient = await ConnectAsync(host.Port);

            var deck = new JsonArray();
            for (var i = 0; i < 39; i++)
            {
                deck.Add("ai.sample.infantry");
            }

            var claimHost = await hostClient.CallAsync("room.claim", new JsonObject
            {
                ["seat"] = 0,
                ["kind"] = "local",
                ["name"] = "房主",
                ["deck"] = deck.DeepClone(),
            });
            Assert.True(claimHost.Ok, claimHost.Message);

            await guestClient.CallAsync("room.claim", new JsonObject
            {
                ["seat"] = 1,
                ["kind"] = "remote",
                ["name"] = "客人",
                ["deck"] = deck.DeepClone(),
            });

            var start = await hostClient.CallAsync("room.start", new JsonObject
            {
                ["seatToken"] = claimHost.Details!["seatToken"]!.GetValue<string>(),
            });
            Assert.True(start.Ok, start.Message);

            await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
            await guestClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });

            var enemyPending = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostGotTargeting = false;

            guestClient.NotificationReceived += notification =>
            {
                if (notification.Method == ProtocolMethods.NotifyTargetRequest && notification.Params is { } parameters)
                {
                    enemyPending.TrySetResult(parameters);
                }
            };

            hostClient.NotificationReceived += notification =>
            {
                if (notification.Method == ProtocolMethods.NotifyTargetRequest)
                {
                    hostGotTargeting = true;
                }
            };

            var match = host.Service.ActiveMatch!;
            var enemy = match.Players[1];

            // 直接发起一次归属座位 1 的选靶（不等完成；只验证推送路由）。
            _ = match.TargeterManager.RunAsync(async flow =>
            {
                await flow.Step(
                    SelectorTemplates.FieldUnit,
                    new ReferenceSetParameter(Array.Empty<Ref<Entity>>(), 0, 1)
                    {
                        Owner = enemy,
                    });
                return TargeterResult.Ok();
            });

            var pending = await enemyPending.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, pending["seat"]!.GetValue<int>());
            Assert.False(hostGotTargeting);

            await guestClient.CallAsync("target.cancel", new JsonObject
            {
                ["sessionId"] = pending["sessionId"]!.GetValue<string>(),
            });
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }

    [Fact]
    public async Task TargetingTimeout_PushesExpired_ToOwningSeatOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-target-expired", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var host = await ServerBootstrap.StartAsync(new ServerOptions
            {
                PackDirectories = [SamplePackDirectory()],
                SandboxRoot = root,
                Port = 0,
                EnforceDeckRules = false,
                TargetSelectionTimeout = TimeSpan.FromSeconds(1),
            });

            await using var hostClient = await ConnectAsync(host.Port);
            await using var guestClient = await ConnectAsync(host.Port);

            var deck = new JsonArray();
            for (var i = 0; i < 39; i++)
            {
                deck.Add("ai.sample.infantry");
            }

            var claimHost = await hostClient.CallAsync("room.claim", new JsonObject
            {
                ["seat"] = 0,
                ["kind"] = "local",
                ["name"] = "房主",
                ["deck"] = deck.DeepClone(),
            });
            Assert.True(claimHost.Ok, claimHost.Message);

            await guestClient.CallAsync("room.claim", new JsonObject
            {
                ["seat"] = 1,
                ["kind"] = "remote",
                ["name"] = "客人",
                ["deck"] = deck.DeepClone(),
            });

            var start = await hostClient.CallAsync("room.start", new JsonObject
            {
                ["seatToken"] = claimHost.Details!["seatToken"]!.GetValue<string>(),
            });
            Assert.True(start.Ok, start.Message);

            await hostClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 0 });
            await guestClient.CallAsync("mulligan.done", new JsonObject { ["owner"] = 1 });

            var expired = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostSawExpired = false;

            guestClient.NotificationReceived += notification =>
            {
                if (notification.Method == ProtocolMethods.NotifyTargetExpired && notification.Params is { } parameters)
                {
                    expired.TrySetResult(parameters);
                }
            };

            hostClient.NotificationReceived += notification =>
            {
                if (notification.Method == ProtocolMethods.NotifyTargetExpired)
                {
                    hostSawExpired = true;
                }
            };

            var match = host.Service.ActiveMatch!;
            _ = match.TargeterManager.RunAsync(async flow =>
            {
                await flow.Step(
                    SelectorTemplates.FieldUnit,
                    new ReferenceSetParameter(Array.Empty<Ref<Entity>>(), 0, 1) { Owner = match.Players[1] });
                return TargeterResult.Ok();
            });

            var payload = await expired.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, payload["seat"]!.GetValue<int>());
            Assert.False(hostSawExpired);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }

    private static async Task<OrcClient> ConnectAsync(int port)
    {
        var client = await OrcClient.ConnectAsync(new ClientOptions { Host = "127.0.0.1", Port = port });
        await client.InitializeAsync(new InitializeParams { ClientName = "cross-seat" });
        return client;
    }

    private static string SamplePackDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }
}
