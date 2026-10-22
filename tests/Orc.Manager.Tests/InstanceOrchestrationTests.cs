using System.Diagnostics;
using System.Text.Json.Nodes;
using Orc.Protocol;
using Xunit;

namespace Orc.Manager.Tests;

/// <summary>
/// S1 实例编排（R1）：真子进程 spawn / 并行多实例 / 单实例销毁与重载（换进程）／端口复用。
/// <para>这些用例会真的起 <c>dotnet Orc.Server.dll</c>，因此断言都落在"可观测的进程事实"上。</para>
/// </summary>
public class InstanceOrchestrationTests
{
    [Fact]
    public async Task Spawn_List_And_Kill_ReapsChildProcess()
    {
        var manager = NewManager();
        try
        {
            var spawn = await manager.SpawnAsync(NewRequest("inst-a"));
            Assert.True(spawn.Ok, $"{spawn.Code}: {spawn.Message}");

            var port = spawn.Payload!["port"]!.GetValue<int>();
            var pid = spawn.Payload["processId"]!.GetValue<int>();
            Assert.True(port > 0);
            Assert.False(Process.GetProcesses().All(p => p.Id != pid));

            var list = manager.List();
            Assert.Single(list);
            Assert.Equal("inst-a", list[0].InstanceId);
            Assert.True(list[0].Running);

            // 管理面经父子私有通道可达。
            var info = await manager.AdminAsync("inst-a", new ManagerAdminCall(Orc.Server.Admin.AdminOps.InstanceInfo));
            Assert.True(info.Ok, info.Message);
            Assert.Equal(port, info.Payload!["port"]!.GetValue<int>());

            var kill = await manager.KillAsync("inst-a");
            Assert.True(kill.Ok, kill.Message);
            Assert.Empty(manager.List());

            await WaitForPortAsync(port, shouldBeOpen: false);
            Assert.True(IsPortFree(port), $"端口 {port} 未被释放（残留监听）。");
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task TwoInstances_RunIndependently()
    {
        var manager = NewManager();
        try
        {
            var first = await manager.SpawnAsync(NewRequest("inst-1"));
            var second = await manager.SpawnAsync(NewRequest("inst-2"));

            Assert.True(first.Ok, first.Message);
            Assert.True(second.Ok, second.Message);

            var ports = manager.List().Select(info => info.Port).ToArray();
            Assert.Equal(2, ports.Length);
            Assert.NotEqual(ports[0], ports[1]);

            // 销毁其中一个：另一个仍在服务（管理面照常应答）。
            Assert.True((await manager.KillAsync("inst-1")).Ok);

            var info = await manager.AdminAsync("inst-2", new ManagerAdminCall(Orc.Server.Admin.AdminOps.InstanceInfo));
            Assert.True(info.Ok, info.Message);
            Assert.True(manager.List().Single().Running);
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reload_ReplacesProcess_AndReusesPort()
    {
        var manager = NewManager();
        try
        {
            var spawn = await manager.SpawnAsync(NewRequest("inst-r"));
            Assert.True(spawn.Ok, spawn.Message);

            var oldPid = spawn.Payload!["processId"]!.GetValue<int>();
            var port = spawn.Payload["port"]!.GetValue<int>();

            var reload = await manager.ReloadAsync("inst-r");
            Assert.True(reload.Ok, reload.Message);

            var newPid = reload.Payload!["processId"]!.GetValue<int>();
            Assert.NotEqual(oldPid, newPid);
            Assert.True(reload.Payload["portReusable"]!.GetValue<bool>(), "重载后旧端口未释放。");

            // 新进程仍持有同一端口（换进程不换接入面）。
            Assert.Equal(port, reload.Payload["port"]!.GetValue<int>());
            Assert.True(await WaitForPortAsync(port, shouldBeOpen: true));

            // 旧进程已被彻底回收。
            Assert.True(Process.GetProcesses().All(p => p.Id != oldPid), "旧服务器进程仍是活的。");

            Assert.True((await manager.KillAsync("inst-r")).Ok);
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task Spawn_WithMissingPack_FailsWithInstanceBootFailed()
    {
        var manager = NewManager();
        try
        {
            var request = NewRequest("inst-bad") with
            {
                PackDirectories = [Path.Combine(Path.GetTempPath(), "no-such-pack-" + Guid.NewGuid().ToString("N"))],
            };

            var spawn = await manager.SpawnAsync(request);

            Assert.False(spawn.Ok);
            Assert.Equal(ManagerCodes.InstanceBootFailed, spawn.Code);
            Assert.Empty(manager.List());
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- 辅助

    private static ServerManager NewManager()
        => new(Path.Combine(AppContext.BaseDirectory, "Orc.Server.dll"));

    private static SpawnRequest NewRequest(string instanceId) => new()
    {
        InstanceId = instanceId,
        PackDirectories = [AssetLibraryTests.SamplePackDirectory()],
        Seats = [new SeatSpawn { Kind = "agent" }, new SeatSpawn { Kind = "agent" }],
        Mode = "play",
        DeckRules = "none",
        Label = instanceId,
        StartupTimeout = TimeSpan.FromSeconds(30),
    };

    private static async Task<bool> WaitForPortAsync(int port, bool shouldBeOpen)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var open = !IsPortFree(port);
            if (open == shouldBeOpen)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return !IsPortFree(port) == shouldBeOpen;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
