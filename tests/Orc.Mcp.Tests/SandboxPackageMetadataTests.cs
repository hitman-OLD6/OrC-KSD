using Orc.Mcp.Sandbox;
using Orc.Packages.Loading;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 沙盒包元数据同步（sidecar ＋ lock）的护栏。
/// <para>
/// 回归背景：装配链由 lock 驱动，而 <see cref="SandboxEnvironment.EnsureScaffold()"/> 只产出
/// <c>manifest.json</c> ＋ 占位卡，**不产出 sidecar 与 lock**。缺这一步时沙盒包会被判非法、
/// 进而被开局装配的降级路径整包剔除——表现为「调试面造出的卡永远进不了对局」。
/// 本组用例锁死：同步之后沙盒包必须能被 <see cref="MatchCardAssembly.Register"/> 接受，
/// 且新增的卡必须出现在卡池里。
/// </para>
/// </summary>
public sealed class SandboxPackageMetadataTests
{
    [Fact]
    public void EnsurePackageMetadata_CreatesLockAndSidecars()
    {
        using var fixture = new SandboxFixture();
        var environment = fixture.Service.Sandbox;

        environment.EnsureScaffold();
        var metadata = environment.EnsurePackageMetadata();

        Assert.True(metadata.Wrote, "首次同步应写入 sidecar 与 lock。");
        Assert.Empty(metadata.Obstacles);

        Assert.True(
            File.Exists(Path.Combine(environment.PackRoot, "lock.formal.json")),
            "沙盒包必须产出 lock.formal.json（装配链的入口）。");

        var placeholderSidecar = Orc.Packages.AssetSidecarJson.SidecarPathFor(environment.PlaceholderCardFile);
        Assert.True(File.Exists(placeholderSidecar), "每个源文件都要有 sidecar（*.meta.json）。");
    }

    [Fact]
    public void EnsurePackageMetadata_IsIdempotent()
    {
        using var fixture = new SandboxFixture();
        var environment = fixture.Service.Sandbox;

        environment.EnsureScaffold();

        Assert.True(environment.EnsurePackageMetadata().Wrote);
        Assert.False(environment.EnsurePackageMetadata().Wrote, "第二次同步应全 Skip。");
    }

    [Fact]
    public void EnsurePackageMetadata_MakesScaffoldLoadable()
    {
        using var fixture = new SandboxFixture();
        var environment = fixture.Service.Sandbox;

        environment.EnsureScaffold();
        environment.EnsurePackageMetadata();

        // 不抛＝沙盒包已是一个可装配的合法卡包。
        var assembly = MatchCardAssembly.Register(new[] { environment.PackRoot });

        Assert.Contains(
            assembly.Definitions,
            entry => entry.Id.EndsWith("/" + fixture.Options.PlaceholderCardId, StringComparison.Ordinal));
    }

    [Fact]
    public void EnsurePackageMetadata_IncludesNewlyWrittenCard()
    {
        using var fixture = new SandboxFixture();
        var environment = fixture.Service.Sandbox;

        environment.EnsureScaffold();
        environment.EnsurePackageMetadata();

        const string cardId = "diy-metadata-probe";
        var cardFile = Path.Combine(environment.CardsDirectory, cardId + ".card.json");
        File.WriteAllText(cardFile, CardJson(cardId));

        var metadata = environment.EnsurePackageMetadata();
        Assert.True(metadata.Wrote, "新增卡后必须重算 lock。");
        Assert.Empty(metadata.Obstacles);

        var assembly = MatchCardAssembly.Register(new[] { environment.PackRoot });
        Assert.Contains(
            assembly.Definitions,
            entry => entry.Id.EndsWith("/" + cardId, StringComparison.Ordinal));
    }

    [Fact]
    public void EnsurePackageMetadata_ReportsUnparsableCardWithoutBreakingPack()
    {
        using var fixture = new SandboxFixture();
        var environment = fixture.Service.Sandbox;

        environment.EnsureScaffold();
        environment.EnsurePackageMetadata();

        // 故意写坏一张卡：只报告障碍，不阻断整包（既有 sidecar 不被写坏）。
        var brokenFile = Path.Combine(environment.CardsDirectory, "broken.card.json");
        File.WriteAllText(brokenFile, "{ not json");

        var metadata = environment.EnsurePackageMetadata();
        Assert.NotEmpty(metadata.Obstacles);

        // 坏卡被跳过，包仍可装配。
        var assembly = MatchCardAssembly.Register(new[] { environment.PackRoot });
        Assert.Contains(
            assembly.Definitions,
            entry => entry.Id.EndsWith("/" + fixture.Options.PlaceholderCardId, StringComparison.Ordinal));
    }

    private static string CardJson(string id) => $$"""
{
  "schemaVersion": 1,
  "id": "{{id}}",
  "name": "元数据同步探针卡",
  "components": [
    { "component": "typeCategory", "type": ["infantry"] },
    { "component": "factionCost", "faction": "neutral", "kredits": 1 },
    { "component": "battleStats", "operationCost": 1, "attack": 1, "defense": 1 },
    { "component": "tagData", "rarity": "standard", "tags": ["sandbox"] }
  ]
}
""";
}
