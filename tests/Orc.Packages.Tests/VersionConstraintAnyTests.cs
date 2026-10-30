using Orc.Core;
using Orc.Packages;
using Xunit;
using static Orc.Packages.Tests.AssetLoadTestKit;

namespace Orc.Packages.Tests;

/// <summary>
/// S2 验收（R4 Constraints 第 4 条／R8 ＋ D1）：版本无关的依赖形态。
/// 三段硬约束不变，只把**首段**放宽为「整数 或 <c>*</c>」；存量三种写法的解析/匹配/输出逐字不变。
/// </summary>
public class VersionConstraintAnyTests
{
    [Fact]
    public void 星主版_解析为Any且往返()
    {
        Assert.True(AssetVersionConstraint.TryParse("*.*.*", out var constraint));
        Assert.Equal(VersionSegmentKind.Any, constraint.MajorKind);
        Assert.Equal(0, constraint.Major);
        Assert.Equal("*.*.*", constraint.ToString());
        Assert.True(AssetVersionConstraint.TryParse(constraint.ToString(), out var roundTrip));
        Assert.Equal(constraint, roundTrip);
    }

    [Fact]
    public void 星主版不等于零版本()
    {
        Assert.True(AssetVersionConstraint.TryParse("*.*.*", out var any));
        Assert.True(AssetVersionConstraint.TryParse("0.0.0", out var zero));

        Assert.False(any.Equals(zero));
        Assert.False(zero.Equals(any));
        Assert.NotEqual(any.GetHashCode(), zero.GetHashCode());

        // 语义也分开：0.0.0 只吃 0.x.y；*.*.* 通吃。
        Assert.True(any.IsSatisfiedBy(AssetVersion.Parse("7.5.3")));
        Assert.False(zero.IsSatisfiedBy(AssetVersion.Parse("7.5.3")));
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("12.0.99")]
    public void 星主版_任意版本都满足(string version)
    {
        Assert.True(AssetVersionConstraint.TryParse("*.*.*", out var constraint));
        Assert.True(constraint.IsSatisfiedBy(AssetVersion.Parse(version)));
    }

    [Fact]
    public void 星主版_其余两段照旧约束()
    {
        Assert.True(AssetVersionConstraint.TryParse("*.*.3", out var patchExact));
        Assert.Equal("*.*.3", patchExact.ToString());
        Assert.True(patchExact.IsSatisfiedBy(AssetVersion.Parse("2.0.3")));
        Assert.True(patchExact.IsSatisfiedBy(AssetVersion.Parse("5.9.3")));
        Assert.False(patchExact.IsSatisfiedBy(AssetVersion.Parse("2.0.4")));

        // `^` 在下界段＝「该段 ≥ 下界」：`*.^2.*` 只约束次版。
        Assert.True(AssetVersionConstraint.TryParse("*.^2.*", out var minorAtLeast));
        Assert.Equal("*.^2.*", minorAtLeast.ToString());
        Assert.True(minorAtLeast.IsSatisfiedBy(AssetVersion.Parse("9.2.0")));
        Assert.False(minorAtLeast.IsSatisfiedBy(AssetVersion.Parse("9.1.9")));

        // `^.` 省略数字＝下界 0（任意），因此 `*.^.*` 三段全放开。
        Assert.True(AssetVersionConstraint.TryParse("*.^.*", out var allOpen));
        Assert.True(allOpen.IsSatisfiedBy(AssetVersion.Parse("123.456.789")));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("1.2")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("^.*.*")]
    [InlineData("-1.0.0")]
    [InlineData("1.2.3.4")]
    public void 非法写法仍解析失败(string text)
    {
        Assert.False(AssetVersionConstraint.TryParse(text, out _));
    }

    [Fact]
    public void 默认值仍是零版本()
    {
        var constraint = default(AssetVersionConstraint);

        Assert.Equal(VersionSegmentKind.Exact, constraint.MajorKind);
        Assert.Equal(0, constraint.Major);
        Assert.Equal("0.0.0", constraint.ToString());
        Assert.True(constraint.IsSatisfiedBy(AssetVersion.Parse("0.0.0")));
        Assert.False(constraint.IsSatisfiedBy(AssetVersion.Parse("1.0.0")));
    }

    [Theory]
    // 主版精确：跨主版不匹配（D1 明令第二/三段规则一字不改）。
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("1.2.3", "2.2.3", false)]
    [InlineData("1.*.*", "1.9.9", true)]
    [InlineData("1.*.*", "2.0.0", false)]
    [InlineData("1.^.*", "1.0.0", true)]
    [InlineData("1.^.*", "1.7.2", true)]
    [InlineData("1.^.*", "0.9.0", false)]
    public void 存量写法语义与输出逐字不变(string text, string version, bool expected)
    {
        Assert.True(AssetVersionConstraint.TryParse(text, out var constraint));
        Assert.Equal(text, constraint.ToString());
        Assert.Equal(expected, constraint.IsSatisfiedBy(AssetVersion.Parse(version)));
    }

    [Fact]
    public void 主版升级后卡组依赖不产生版本不符且层号更大()
    {
        var registry = new AssetRegistry();

        // 卡先以 1.0.0 存在，随后升主版到 2.0.0（旧版本仍注册：升版不删历史）。
        Register(registry, "p", "rifle", version: "1.0.0");
        var upgraded = Register(registry, "p", "rifle", version: "2.0.0");

        // 卡组按派生依赖 `*.*.*` 指向该卡（S8 的重算结果就长这样）。
        var deck = Register(
            registry,
            "p",
            "starter",
            kind: AssetKind.Deck,
            version: "1.0.0",
            dependsOn: [Depends(AssetKind.Card, "pack/p/rifle", "*.*.*")]);

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", upgraded, deck)]);

        Assert.True(plan.IsValid);
        Assert.DoesNotContain(plan.Issues, issue => issue.Code == AssetLoadIssueCode.DependencyVersionMismatch);

        var layerOf = plan.Formal.Layers
            .SelectMany(layer => layer.Items.Select(item => (item.AppId, layer.Index)))
            .ToDictionary(pair => pair.AppId, pair => pair.Index);
        Assert.True(
            layerOf["pack/p/starter"] > layerOf["pack/p/rifle"],
            "卡组层号须严格大于被引用卡的层号。");
    }

    [Fact]
    public void sidecar里的非法约束仍是包级结构性非法()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-constraint-" + Guid.NewGuid().ToString("N"));
        var packRoot = Path.Combine(root, "packs", "p");
        Directory.CreateDirectory(Path.Combine(packRoot, "cards"));
        try
        {
            var packGuid = Guid.Parse("c0de0000-0000-4000-8000-0000000000c1");
            File.WriteAllText(
                Path.Combine(packRoot, "cards", "rifle.card.json"),
                """
                {
                 "schemaVersion": 1,
                 "id": "rifle",
                 "name": "示例步枪",
                 "components": [ { "type": "factionCost", "faction": "germany", "deployCost": 1 } ]
                }
                """);

            var version = AssetVersion.Parse("1.0.0");
            var metadata = new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(packGuid, AssetKind.Card, "rifle", version),
                AssetKind.Card,
                "rifle",
                version,
                Anchor: null,
                Loader: null,
                DependsOn: [new AssetReference(AssetKind.Card, "pack/p/other", "*")],
                ConflictsWith: []);
            AssetSidecarJson.WriteFile(
                Path.Combine(packRoot, "cards", "rifle.card.json" + AssetSidecarJson.SidecarExtension),
                new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, "p", "cards/rifle.card.json", [metadata]));

            var manifest = new PackageManifest(
                PackageKind.CardPack,
                "p",
                packGuid,
                PackageVersion.Parse("1.0.0"),
                Array.Empty<PackageDependency>(),
                Array.Empty<PackageAsset>(),
                Name: "约束示例",
                Metadata: Array.Empty<string>());

            var failure = Assert.Throws<InvalidDataException>(
                () => PackageRegistrar.Register(packRoot, manifest, new AssetRegistry()));
            Assert.Contains("非法", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
