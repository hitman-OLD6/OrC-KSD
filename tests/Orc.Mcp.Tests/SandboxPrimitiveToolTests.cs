using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 沙盒原语工具验收（R5）：①未开 <c>--allow-debug-writes</c> 时不在清单；②返回的新建 ref 可直接用于下一步；
/// ③参数错误＝<c>invalid-arguments</c>；④引擎拒绝＝<c>engine-rejected</c> ＋ <c>details.reason</c>；
/// ⑤点数原语的两类 warning；⑥旧工具契约零变更。
/// </summary>
public class SandboxPrimitiveToolTests
{
    private const string PrimitiveUnitCardJson = """
{
  "schemaVersion": 1,
  "id": "sbx.primitive.unit",
  "name": "原语步兵",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 1 },
    { "component": "battleStats", "operationCost": 0, "attack": 2, "defense": 3 },
    { "component": "tagData", "rarity": "Standard" }
  ]
}
""";

    [Fact]
    public void ToolsHidden_WithoutAllowDebugWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new McpServiceOptions
            {
                Mode = McpServiceMode.Authoring,
                SandboxRoot = root,
                AllowDebugWrites = false,
                Seed = 1234,
            };
            var service = new McpService(options);

            var names = service.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var primitive in new[]
                     {
                         "sandbox.instantiate", "sandbox.unitize", "sandbox.move",
                         "sandbox.remove", "sandbox.points", "sandbox.draw",
                     })
            {
                Assert.DoesNotContain(primitive, names);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReturnedRefIsUsableByNextCall()
    {
        using var fixture = new SandboxFixture();
        await CreateMatchWithUnitDefinitionAsync(fixture, PrimitiveUnitCardJson, "sbx.primitive.unit");

        var instantiated = await fixture.InvokeAsync(
            "sandbox.instantiate",
            """{"definitionId":"sbx.primitive.unit","owner":0,"zone":"hand"}""");
        Assert.True(instantiated.Ok, instantiated.Message);
        var reference = instantiated.EngineResult!["ref"]!.GetValue<string>();

        // 下一步直接使用上一步返回的 ref（可链式调用）。
        var unitized = await fixture.InvokeAsync(
            "sandbox.unitize",
            $$"""{"ref":"{{reference}}","line":"support","slot":0}""");

        Assert.True(unitized.Ok, unitized.Message);
        Assert.Equal(0, unitized.EngineResult!["slot"]!.GetValue<int>());
        Assert.Equal("support", unitized.EngineResult!["line"]!.GetValue<string>());

        var moved = await fixture.InvokeAsync(
            "sandbox.move",
            $$"""{"ref":"{{reference}}","line":"frontline","slot":2}""");
        Assert.True(moved.Ok, moved.Message);
        Assert.Equal(2, moved.EngineResult!["slot"]!.GetValue<int>());
    }

    [Fact]
    public async Task InvalidArguments_Code()
    {
        using var fixture = new SandboxFixture();
        await CreateMatchWithUnitDefinitionAsync(fixture, PrimitiveUnitCardJson, "sbx.primitive.unit");

        var missing = await fixture.InvokeAsync("sandbox.instantiate", """{"owner":0,"zone":"hand"}""");
        Assert.False(missing.Ok);
        Assert.Equal(McpCodes.InvalidArguments, missing.Code);

        var badSlot = await fixture.InvokeAsync(
            "sandbox.instantiate",
            """{"definitionId":"sbx.primitive.unit","owner":0,"zone":"support"}""");
        Assert.False(badSlot.Ok);
        Assert.Equal(McpCodes.InvalidArguments, badSlot.Code);
    }

    [Fact]
    public async Task EngineRejected_CodeWithReason()
    {
        using var fixture = new SandboxFixture();
        await CreateMatchWithUnitDefinitionAsync(fixture, PrimitiveUnitCardJson, "sbx.primitive.unit");

        var rejected = await fixture.InvokeAsync(
            "sandbox.instantiate",
            """{"definitionId":"does.not.exist","owner":0,"zone":"hand"}""");

        Assert.False(rejected.Ok);
        Assert.Equal(McpCodes.EngineRejected, rejected.Code);
        Assert.Equal("definition-unknown", rejected.EngineResult!["reason"]!.GetValue<string>());
        Assert.NotEqual(McpCodes.Internal, rejected.Code);
    }

    [Fact]
    public async Task Points_ExceedsSlots_Warning()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var result = await fixture.InvokeAsync("sandbox.points", """{"owner":0,"points":40}""");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(40, result.EngineResult!["after"]!.GetValue<int>());
        Assert.Equal("points-exceed-slots", result.EngineResult!["warning"]!.GetValue<string>());
    }

    [Fact]
    public async Task Points_SameValue_NoSignal()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var view = await fixture.InvokeInContextAsync("observe.view");
        var current = view.Details!["players"]!.AsArray()[0]!["points"]!.GetValue<int>();

        var segmentsBefore = (await fixture.InvokeAsync("observe.segments")).Details!["cachedTotal"]!.GetValue<int>();

        var result = await fixture.InvokeAsync("sandbox.points", $$"""{"owner":0,"points":{{current}}}""");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, result.EngineResult!["delta"]!.GetValue<int>());
        Assert.Null(result.EngineResult!["warning"]);

        var segmentsAfter = (await fixture.InvokeAsync("observe.segments")).Details!["cachedTotal"]!.GetValue<int>();
        Assert.Equal(segmentsBefore, segmentsAfter); // 同值＝零信号、零段
    }

    [Fact]
    public async Task LegacySetPoints_ContractUnchanged()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.CreateSandboxMatchAsync()).Ok);

        var legacy = await fixture.InvokeAsync("sandbox.set_points", """{"owner":0,"delta":3}""");

        Assert.True(legacy.Ok, legacy.Message);
        // 旧契约：owner / delta / points / pointSlots（无 ok/ref/before/after 之外的新字段）
        Assert.Equal(0, legacy.EngineResult!["owner"]!.GetValue<int>());
        Assert.Equal(3, legacy.EngineResult!["delta"]!.GetValue<int>());
        Assert.True(legacy.EngineResult!["points"]!.GetValue<int>() >= 3);
        Assert.NotNull(legacy.EngineResult!["pointSlots"]);
        Assert.Null(legacy.EngineResult!["reason"]);
    }

    [Fact]
    public async Task ExistingSandboxTools_ContractUnchanged()
    {
        using var fixture = new SandboxFixture();
        await CreateMatchWithUnitDefinitionAsync(fixture, PrimitiveUnitCardJson, "sbx.primitive.unit");

        // spawn：ref / definitionId / owner / cardName / zone / placeStatus（与引入原语前逐字一致）。
        var spawned = await fixture.InvokeAsync(
            "sandbox.spawn", """{"definitionId":"sbx.primitive.unit","owner":0,"zone":"hand"}""");
        Assert.True(spawned.Ok, spawned.Message);
        var reference = spawned.EngineResult!["ref"]!.GetValue<string>();
        Assert.Equal("sbx.primitive.unit", spawned.EngineResult!["definitionId"]!.GetValue<string>());
        Assert.Equal(0, spawned.EngineResult!["owner"]!.GetValue<int>());
        Assert.Equal("hand", spawned.EngineResult!["zone"]!.GetValue<string>());
        Assert.NotNull(spawned.EngineResult!["placeStatus"]);
        Assert.NotNull(spawned.EngineResult!["cardName"]);

        // damage：ref / amount / lethal / alive。
        var damaged = await fixture.InvokeAsync("sandbox.damage", $$"""{"ref":"{{reference}}","amount":1}""");
        Assert.True(damaged.Ok, damaged.Message);
        Assert.Equal(1, damaged.EngineResult!["amount"]!.GetValue<int>());
        Assert.NotNull(damaged.EngineResult!["lethal"]);
        Assert.NotNull(damaged.EngineResult!["alive"]);

        // kill：ref / destroyed / alive。
        var killed = await fixture.InvokeAsync("sandbox.kill", $$"""{"ref":"{{reference}}"}""");
        Assert.True(killed.Ok, killed.Message);
        Assert.True(killed.EngineResult!["destroyed"]!.GetValue<bool>());
        Assert.False(killed.EngineResult!["alive"]!.GetValue<bool>());

        // emit：event / subscriberCount / hasSubscriber。
        var emitted = await fixture.InvokeAsync("sandbox.emit", """{"event":"card.placed"}""");
        Assert.True(emitted.Ok, emitted.Message);
        Assert.Equal("card.placed", emitted.EngineResult!["event"]!.GetValue<string>());
        Assert.NotNull(emitted.EngineResult!["subscriberCount"]);
        Assert.NotNull(emitted.EngineResult!["hasSubscriber"]);
    }

    // ---------- 共用 ----------

    /// <summary>建沙盒局 ＋ 写入一张卡并 card.load 注册（原语用例的公共前置）。</summary>
    internal static async Task CreateMatchWithUnitDefinitionAsync(
        SandboxFixture fixture, string cardJson, string definitionId, string? prefabJson = null)
    {
        if (!fixture.Service.Executor.HasMatch)
        {
            var created = await fixture.CreateSandboxMatchAsync();
            Assert.True(created.Ok, created.Message);
        }

        var files = new JsonArray { $"pack/cards/{definitionId}.card.json" };
        var written = await fixture.InvokeAsync(
            "card.write",
            new JsonObject
            {
                ["path"] = $"pack/cards/{definitionId}.card.json",
                ["json"] = cardJson,
            }.ToJsonString());
        Assert.True(written.Ok, written.Message);

        if (prefabJson is not null)
        {
            var prefabPath = $"pack/effects/{definitionId}.effect.prefab.json";
            var prefab = await fixture.InvokeAsync(
                "card.write",
                new JsonObject { ["path"] = prefabPath, ["json"] = prefabJson }.ToJsonString());
            Assert.True(prefab.Ok, prefab.Message);
            files.Add(prefabPath);
        }

        var loaded = await fixture.InvokeAsync(
            "card.load",
            new JsonObject { ["files"] = files }.ToJsonString());

        Assert.True(loaded.Ok, loaded.Message);
        var registered = loaded.EngineResult!["registered"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        Assert.Contains(definitionId, registered);
    }

    /// <summary>观察面：指定玩家的手牌 ref 列表。</summary>
    internal static string[] HandRefs(JsonNode? details, int playerIndex)
        => details!["players"]!.AsArray()[playerIndex]!["hand"]!.AsArray()
            .Select(node => node!["ref"]!.GetValue<string>())
            .ToArray();

    /// <summary>观察面：指定玩家的点数。</summary>
    internal static int PointsOf(JsonNode? details, int playerIndex)
        => details!["players"]!.AsArray()[playerIndex]!["points"]!.GetValue<int>();
}
