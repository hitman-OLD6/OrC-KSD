using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// 资产化 S5 验收（S4 修订）：manifest 读写（往返一致／非法拒绝）、包级 dependsOn 读宽写窄、
/// 资产名派生（包 GUID8 继承 ＋ 资产版本自持——包版本是内容指纹，不参与身份）。
/// </summary>
public class PackageManifestTests
{
    private static readonly Guid SampleGuid = Guid.Parse("ab12cd34-0000-0000-0000-000000000000");

    private static PackageManifest SampleManifest() => new(
        PackageKind.Plugin,
        "orc.core.keywords",
        SampleGuid,
        PackageVersion.Parse("1.2.0"),
        new[] { new PackageDependency("orc.core.base", "1.*.*") },
        new[]
        {
            new PackageAsset("csx", "faction.service", "csx/faction.csx"),
            new PackageAsset("json", "faction.table", "data/factions.json"),
        });

    [Fact]
    public void Manifest_读写往返一致_且pack版本为内容指纹()
    {
        var manifest = SampleManifest();

        var parsed = PackageManifest.Parse(manifest.ToJson(), out var warnings);

        Assert.Equal(manifest.Kind, parsed.Kind);
        Assert.Equal(manifest.Id, parsed.Id);
        Assert.Equal(manifest.Guid, parsed.Guid);
        Assert.Equal(manifest.Version, parsed.Version);
        Assert.Equal(manifest.Assets, parsed.Assets);
        Assert.Empty(parsed.Dependencies); // dependsOn 写窄：不再产出
        Assert.Empty(warnings);
    }

    [Fact]
    public void 旧manifest含dependsOn_可解析_仅迁移告警()
    {
        var json = """
        {
          "schemaVersion": 2,
          "id": "p1",
          "guid": "ab12cd34-0000-0000-0000-000000000000",
          "kind": "plugin",
          "version": "1.0.0",
          "dependsOn": [{ "id": "orc.core.base", "version": "1.*.*" }]
        }
        """;

        var parsed = PackageManifest.Parse(json, out var warnings);

        Assert.Single(parsed.Dependencies);         // 读宽：仍可解析
        var warning = Assert.Single(warnings);      // 仅迁移告警（不报错）
        Assert.Contains("dependsOn", warning);
        Assert.DoesNotContain("dependsOn", parsed.ToJson()); // 写窄：不再产出
    }

    [Fact]
    public void Guid8_与缓存域_派生正确()
    {
        var manifest = SampleManifest();

        Assert.Equal("ab12cd34", manifest.Guid8);
        Assert.Equal("ab12cd34#1.2.0", manifest.CacheScope);
    }

    [Fact]
    public void 资产名派生_继承包GUID8_资产版本自持()
    {
        var manifest = SampleManifest();

        var name = manifest.AssetNameOf(manifest.Assets[0]);

        Assert.Equal(AssetLoadKind.Csx, name.Kind);
        Assert.Equal("faction.service", name.LocalName);
        Assert.Equal("ab12cd34", name.PackageGuid8);
        Assert.Equal(new AssetVersion(1, 0, 0), name.Version); // 包版本（1.2.0）不参与资产身份
        Assert.Equal("csx：faction.service@ab12cd34#1.0.0", name.ToString());

        var versioned = manifest.AssetNameOf(manifest.Assets[0] with { Version = new AssetVersion(3, 1, 0) });
        Assert.Equal("csx：faction.service@ab12cd34#3.1.0", versioned.ToString());
    }

    [Fact]
    public void 键名大小写不敏感_读宽()
    {
        var json = """
        {
          "Kind": "plugin",
          "ID": "p1",
          "Guid": "ab12cd34-0000-0000-0000-000000000000",
          "Version": "1.0.0"
        }
        """;

        var manifest = PackageManifest.Parse(json);

        Assert.Equal("p1", manifest.Id);
        Assert.Empty(manifest.Assets);
        Assert.Empty(manifest.Dependencies);
    }

    [Theory]
    [InlineData("""{"kind":"plugin","id":"p","guid":"not-a-guid","version":"1.0.0"}""")]
    [InlineData("""{"kind":"plugin","id":"p","guid":"ab12cd34-0000-0000-0000-000000000000","version":"1.2/3"}""")]
    [InlineData("""{"kind":"nope","id":"p","guid":"ab12cd34-0000-0000-0000-000000000000","version":"1.0.0"}""")]
    [InlineData("""{"kind":"plugin","id":"","guid":"ab12cd34-0000-0000-0000-000000000000","version":"1.0.0"}""")]
    public void 非法manifest_明确拒绝(string json)
    {
        Assert.Throws<FormatException>(() => PackageManifest.Parse(json));
    }

    [Fact]
    public void manifest_v2_写出含元数据列表_且往返一致()
    {
        var manifest = new PackageManifest(
            PackageKind.CardPack,
            "sample.pack",
            SampleGuid,
            PackageVersion.Parse("1.0.0"),
            Array.Empty<PackageDependency>(),
            Array.Empty<PackageAsset>(),
            Name: "示例卡包",
            Metadata: ["cards/a.card.json.meta.json", "effects/b.prefab.json.meta.json"]);

        var json = manifest.ToJson();

        Assert.Contains("\"schemaVersion\": 2", json);
        Assert.Contains("\"name\": \"示例卡包\"", json);
        Assert.DoesNotContain("dependsOn", json);
        Assert.DoesNotContain("\"assets\"", json);

        var parsed = PackageManifest.Parse(json);
        Assert.Equal(PackageManifest.CurrentSchemaVersion, parsed.SchemaVersion);
        Assert.Equal("示例卡包", parsed.Name);
        Assert.Equal(manifest.MetadataList, parsed.MetadataList);
    }

    [Fact]
    public void 缺guid_结构性拒绝()
    {
        var json = """
        { "schemaVersion": 2, "id": "p1", "kind": "cardpack", "version": "1.0.0", "metadata": [] }
        """;

        var error = Assert.Throws<FormatException>(() => PackageManifest.Parse(json));
        Assert.Contains("guid", error.Message);
    }

    [Fact]
    public void 缺id_结构性拒绝()
    {
        var json = """
        { "schemaVersion": 2, "guid": "ab12cd34-0000-0000-0000-000000000000", "kind": "cardpack", "version": "1.0.0", "metadata": [] }
        """;

        var error = Assert.Throws<FormatException>(() => PackageManifest.Parse(json));
        Assert.Contains("id", error.Message);
    }

    [Fact]
    public void metadata列表重复_拒绝()
    {
        var json = """
        {
          "schemaVersion": 2,
          "id": "p1",
          "guid": "ab12cd34-0000-0000-0000-000000000000",
          "kind": "cardpack",
          "version": "1.0.0",
          "metadata": ["a.card.json.meta.json", "a.card.json.meta.json"]
        }
        """;

        Assert.Throws<FormatException>(() => PackageManifest.Parse(json));
    }

    [Fact]
    public void 旧v1清单_读宽仍可解析()
    {
        var json = """
        {
          "kind": "plugin",
          "id": "p1",
          "guid": "ab12cd34-0000-0000-0000-000000000000",
          "version": "1.0.0",
          "dependsOn": [{ "id": "base", "version": "1.*.*" }],
          "assets": [{ "type": "csx", "name": "a", "path": "a.csx" }]
        }
        """;

        var manifest = PackageManifest.Parse(json);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Single(manifest.Dependencies);
        Assert.Single(manifest.Assets);
        Assert.Empty(manifest.MetadataList);
    }

    [Fact]
    public void 资产短名重复_拒绝()
    {
        var json = """
        {
          "kind": "plugin",
          "id": "p1",
          "guid": "ab12cd34-0000-0000-0000-000000000000",
          "version": "1.0.0",
          "assets": [
            { "type": "csx", "name": "a", "path": "a.csx" },
            { "type": "json", "name": "a", "path": "b.json" }
          ]
        }
        """;

        Assert.Throws<FormatException>(() => PackageManifest.Parse(json));
    }
}
