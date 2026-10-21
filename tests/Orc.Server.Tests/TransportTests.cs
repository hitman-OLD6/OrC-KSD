using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;
using Orc.Protocol;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S8a 传输链路：真 socket（loopback）上的握手、工具目录、工具调用、未知方法。
/// </summary>
public class TransportTests
{
    [Fact]
    public async Task Initialize_ListTools_CallTool_OverRealSocket()
    {
        using var temp = new TempDirectory();
        var service = NewService(temp.Path);
        await using var host = await new ServerHost(service, NewOptions(temp.Path)).StartAsync();

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        var initialized = await client.InitializeAsync(new InitializeParams
        {
            ClientName = "server-tests",
            ProtocolVersion = ProtocolInfo.ProtocolVersion,
        });

        Assert.Equal(ProtocolInfo.ProtocolVersion, initialized.ProtocolVersion);
        Assert.Equal(service.Options.ContractSchemaVersion, initialized.SchemaVersion);
        Assert.Contains("room", initialized.Capabilities);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "room.info");
        Assert.Contains(tools, tool => tool.Name == "room.claim");

        var result = await client.CallAsync("room.info");
        Assert.True(result.Ok);
        Assert.Equal("lobby", result.Details!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        using var temp = new TempDirectory();
        var service = NewService(temp.Path);
        await using var host = await new ServerHost(service, NewOptions(temp.Path)).StartAsync();
        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        var response = await client.CallRawAsync("does/not/exist", null);

        Assert.NotNull(response.Error);
        Assert.Equal(RpcErrorCodes.MethodNotFound, response.Error!.Code);
    }

    [Fact]
    public async Task VersionMismatch_IsRejected()
    {
        using var temp = new TempDirectory();
        var service = NewService(temp.Path);
        await using var host = await new ServerHost(service, NewOptions(temp.Path)).StartAsync();
        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        var response = await client.CallRawAsync(
            ProtocolMethods.Initialize,
            new JsonObject { ["protocolVersion"] = "999.0" });

        Assert.NotNull(response.Error);
        Assert.Contains("版本", response.Error!.Message);
    }

    [Fact]
    public async Task BusinessFailure_StaysInResultChannel()
    {
        using var temp = new TempDirectory();
        var service = NewService(temp.Path);
        await using var host = await new ServerHost(service, NewOptions(temp.Path)).StartAsync();
        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        var result = await client.CallAsync("room.claim", new JsonObject { ["seat"] = 0 });

        Assert.False(result.Ok);
        Assert.Equal(ResultCodes.DeckInvalid, result.Code);
    }

    private static McpService NewService(string root)
        => new(
            new McpServiceOptions
            {
                Mode = McpServiceMode.Player,
                SandboxRoot = root,
                Seed = 7,
            },
            new FakeRoomControl());

    private static ServerOptions NewOptions(string root) => new()
    {
        PackDirectories = [],
        SandboxRoot = root,
        Port = 0,
    };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc-server-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }

    private sealed class FakeRoomControl : IRoomControl
    {
        public RoomSnapshot Snapshot() => new(
            "room-1",
            RoomStates.Lobby,
            [
                new RoomSeatSnapshot(0, ControllerKinds.Local, "房主", true),
                new RoomSeatSnapshot(1, string.Empty, null, false),
            ],
            "loaded",
            null,
            []);

        public McpToolResult Claim(RoomSeatClaim claim)
            => McpToolResult.Failure(McpCodes.DeckInvalid, "卡组为空（卡组必须显式提供）。");

        public McpToolResult Release(RoomSeatRelease release) => McpToolResult.Success("已释放。");

        public McpToolResult AddAi(RoomAiSeat ai) => McpToolResult.Success("已生成。");

        public Task<McpToolResult> StartAsync(string? seatToken, CancellationToken ct = default)
            => Task.FromResult(McpToolResult.Success("已开局。"));

        public McpToolResult Kick(int seat, string? seatToken) => McpToolResult.Success("已释放。");

        public McpToolResult Resume(string seatToken) => McpToolResult.Success("已绑定。");
    }
}
