using Orc.Client;
using Orc.Protocol;
using Orc.Server;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S1 骨架冒烟：确认协议/客户端/服务端三个工程的类型可见、默认值符合已确认口径。
/// 真实用例（房间状态机、协议往返、双客户端 e2e）在 S3/S2/S9 补。
/// </summary>
public class SkeletonSmokeTests
{
    [Fact]
    public void ProtocolVersion_IsDeclared()
        => Assert.False(string.IsNullOrWhiteSpace(ProtocolInfo.ProtocolVersion));

    [Fact]
    public void ServerOptions_Defaults_MatchConfirmedDecisions()
    {
        var options = new ServerOptions
        {
            PackDirectories = ["packs"],
            SandboxRoot = "sandbox",
        };

        Assert.Equal(2, options.SeatCount);
        Assert.Equal(0, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(60), options.DisconnectGrace);
        Assert.Equal(TimeSpan.Zero, options.LobbySeatRetention);
    }

    [Fact]
    public void ClientOptions_Defaults_MatchHeartbeatDecision()
    {
        var options = new ClientOptions { Host = "127.0.0.1", Port = 1 };

        // 只断言口径（行为覆盖见 ClientHeartbeatTests：空闲 ping、超时判死、失败而非挂起）。
        Assert.Equal(TimeSpan.FromSeconds(30), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), options.HeartbeatTimeout);
    }
}
