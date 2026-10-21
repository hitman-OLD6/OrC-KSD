using System.Net;
using System.Net.Sockets;
using Orc.Client;
using Orc.Protocol;
using Orc.Server;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 客户端心跳（F6）：默认值、空闲 ping、超时判死与"失败而非挂起"。
/// </summary>
public class ClientHeartbeatTests
{
    [Fact]
    public void ClientOptions_Defaults_Are30And15Seconds()
    {
        var options = new ClientOptions { Host = "127.0.0.1", Port = 1 };

        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), options.HeartbeatTimeout);
    }

    /// <summary>服务端心跳超时先到（客户端不发 ping）→ 调用抛 IOException，而不是挂起。</summary>
    [Fact]
    public async Task ServerHeartbeatTimeout_FailsCallWithIOException()
    {
        using var temp = new TestSupport.TempDirectory("orc-heartbeat");
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
        });

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
            HeartbeatInterval = TimeSpan.FromHours(1), // 不主动 ping
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
        });

        await Task.Delay(TimeSpan.FromSeconds(4));

        var exception = await Record.ExceptionAsync(
            () => client.CallAsync("room.info").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.NotNull(exception);
        Assert.IsType<IOException>(exception);
        Assert.False(client.IsConnected);
    }

    /// <summary>空闲时客户端主动 ping → 服务端心跳不判死，长静默后仍可用。</summary>
    [Fact]
    public async Task IdleClient_PingsAndStaysConnected()
    {
        using var temp = new TestSupport.TempDirectory("orc-heartbeat");
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
            HeartbeatTimeout = TimeSpan.FromSeconds(3),
        });

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
            HeartbeatInterval = TimeSpan.FromMilliseconds(400),
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
        });

        // 静默 6 秒：服务端超时 3 秒，若客户端不 ping 必被断开。
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.True(client.IsConnected);

        var info = await client.CallAsync("room.info");
        Assert.True(info.Ok, info.Message);
    }

    /// <summary>对端只收不回 → 心跳超时判死：IsConnected=false，挂起请求以 IOException 失败。</summary>
    [Fact]
    public async Task HeartbeatTimeout_WithoutInboundFrame_ClosesAndFailsPending()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();

            await using var client = await OrcClient.ConnectAsync(new ClientOptions
            {
                Host = "127.0.0.1",
                Port = port,
                HeartbeatInterval = TimeSpan.FromMilliseconds(200),
                HeartbeatTimeout = TimeSpan.FromMilliseconds(300),
            });

            using var serverSide = await accept; // 保持连接但永不回帧

            var exception = await Record.ExceptionAsync(
                () => client.CallRawAsync(ProtocolMethods.Ping, null).WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.NotNull(exception);
            Assert.IsType<IOException>(exception);
            Assert.False(client.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }
}
