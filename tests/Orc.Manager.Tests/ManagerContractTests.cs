using System.Text.Json.Nodes;
using Orc.Protocol;
using Xunit;

namespace Orc.Manager.Tests;

/// <summary>
/// S1 入口分层与 S2 码表：CLI 与进程内 API 共用同一个 <c>IManager</c>（同一次调用、逐字等价）；
/// 码表两侧各自有常量源，映射必须一一对应。
/// </summary>
public class ManagerContractTests
{
    [Fact]
    public void Codes_MirrorProtocolCodes()
    {
        Assert.Equal(ResultCodes.Ok, ManagerCodes.Ok);
        Assert.Equal(ResultCodes.InvalidArguments, ManagerCodes.InvalidArguments);
        Assert.Equal(ResultCodes.Busy, ManagerCodes.Busy);
        Assert.Equal(ResultCodes.Internal, ManagerCodes.Internal);
        Assert.Equal(ResultCodes.LockConflict, ManagerCodes.LockConflict);
        Assert.Equal(ResultCodes.MissingAssets, ManagerCodes.MissingAssets);
        Assert.Equal(ResultCodes.InstanceBootFailed, ManagerCodes.InstanceBootFailed);

        // 子进程专有码按原样透传（管理器是超集，不新造同义码）。
        Assert.Equal(ResultCodes.RoomRotating, ManagerCodes.Map(ResultCodes.RoomRotating));
        Assert.Equal(ResultCodes.SeatTaken, ManagerCodes.Map(ResultCodes.SeatTaken));
        Assert.Equal(ManagerCodes.Internal, ManagerCodes.Map(null));
    }

    [Fact]
    public void ManagerCodes_AssetCodes_AreMarked()
    {
        Assert.True(ManagerCodes.IsAssetCode(ManagerCodes.LockConflict));
        Assert.True(ManagerCodes.IsAssetCode(ManagerCodes.MissingAssets));
        Assert.False(ManagerCodes.IsAssetCode(ManagerCodes.InvalidArguments));
    }

    [Fact]
    public async Task Cli_List_MatchesInProcessApi()
    {
        await using var manager = new ServerManager(serverAssembly: "unused");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await Cli.RunAsync(["list"], output, error, () => manager);

        Assert.Equal(0, exit);
        var node = JsonNode.Parse(output.ToString())!;
        Assert.True(node["ok"]!.GetValue<bool>());
        Assert.Empty(node["instances"]!.AsArray());
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task Cli_AssetsRegister_MatchesInProcessAssetLibrary()
    {
        await using var manager = new ServerManager(serverAssembly: "unused");
        var pack = AssetLibraryTests.SamplePackDirectory();

        var output = new StringWriter();
        var exit = await Cli.RunAsync(
            ["assets", "register", "--packs", pack], output, new StringWriter(), () => manager);

        Assert.Equal(0, exit);

        // 同一次调用后，进程内资产库与 CLI 输出描述同一批包（逐字等价）。
        var node = JsonNode.Parse(output.ToString())!;
        Assert.Single(node["registered"]!.AsArray());
        Assert.Single(manager.Assets.Packages);
        Assert.Equal(
            manager.Assets.Packages[0].PackageId,
            node["registered"]![0]!["packageId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cli_UnknownCommand_ReturnsNonZero()
    {
        await using var manager = new ServerManager(serverAssembly: "unused");
        var error = new StringWriter();

        var exit = await Cli.RunAsync(["nope"], new StringWriter(), error, () => manager);

        Assert.Equal(1, exit);
        Assert.Contains("未知命令", error.ToString());
    }

    [Fact]
    public async Task Cli_Kill_UnknownInstance_FailsWithUnknownInstanceCode()
    {
        await using var manager = new ServerManager(serverAssembly: "unused");
        var output = new StringWriter();

        var exit = await Cli.RunAsync(["kill", "--id", "ghost"], output, new StringWriter(), () => manager);

        Assert.Equal(1, exit);
        var node = JsonNode.Parse(output.ToString())!;
        Assert.False(node["ok"]!.GetValue<bool>());
        Assert.Equal(ManagerCodes.UnknownInstance, node["code"]!.GetValue<string>());
    }
}
