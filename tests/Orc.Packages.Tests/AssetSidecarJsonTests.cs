using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S2 验收：元数据 sidecar（R6/D46-D49）——源文件名一对一可逆映射、读写往返语义保真、
/// 写窄（固定键序／2 空格／LF／无 BOM）、读宽（注释／尾逗号／键名大小写）、版本按书写段数原样落盘。
/// </summary>
public class AssetSidecarJsonTests
{
    private static readonly Guid CardGuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid PrefabGuid = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static AssetSidecar SampleSidecar() => new(
        AssetSidecarJson.CurrentSchemaVersion,
        "sample.pack",
        "cards/sample.rifle.card.json",
        [
            new AssetMetadata(
                CardGuid,
                AssetKind.Card,
                "sample.rifle",
                AssetVersion.Parse("1.0.0"),
                Anchor: null,
                Loader: "builtin.card",
                DependsOn: [new AssetReference(AssetKind.Effect, "pack/sample.pack/fire", "1.*.*")],
                ConflictsWith: []),
            new AssetMetadata(
                PrefabGuid,
                AssetKind.Trigger,
                "onPlay",
                AssetVersion.Parse("1.0.0"),
                Anchor: "stableKey.onPlay",
                Loader: null,
                DependsOn: [],
                ConflictsWith: [new AssetReference(AssetKind.Card, "pack/other/pistol", "2.0.0")]),
        ]);

    [Fact]
    public void 读写往返_语义保真()
    {
        var parsed = AssetSidecarJson.Parse(AssetSidecarJson.ToJson(SampleSidecar()));

        Assert.Equal(AssetSidecarJson.CurrentSchemaVersion, parsed.SchemaVersion);
        Assert.Equal("sample.pack", parsed.PackageId);
        Assert.Equal("cards/sample.rifle.card.json", parsed.Source);
        Assert.Equal(2, parsed.Assets.Count);

        var card = parsed.Assets[0];
        Assert.Equal(CardGuid, card.Guid);
        Assert.Equal(AssetKind.Card, card.Kind);
        Assert.Equal("sample.rifle", card.Id);
        Assert.Equal(new AssetVersion(1, 0, 0), card.Version);
        Assert.Null(card.Anchor);
        Assert.Equal("builtin.card", card.Loader);
        Assert.Equal([new AssetReference(AssetKind.Effect, "pack/sample.pack/fire", "1.*.*")], card.DependsOn);
        Assert.Empty(card.ConflictsWith);

        var trigger = parsed.Assets[1];
        Assert.Equal(AssetKind.Trigger, trigger.Kind);
        Assert.Equal("stableKey.onPlay", trigger.Anchor);
        Assert.Null(trigger.Loader);
        Assert.Empty(trigger.DependsOn);
        Assert.Single(trigger.ConflictsWith);
        Assert.Equal("pack/other/pistol", trigger.ConflictsWith[0].AppId);
    }

    [Fact]
    public void 写窄_固定键序与二空格缩进与LF()
    {
        var json = AssetSidecarJson.ToJson(SampleSidecar());

        var expected = string.Join("\n",
        [
            "{",
            "  \"schemaVersion\": 1,",
            "  \"packageId\": \"sample.pack\",",
            "  \"source\": \"cards/sample.rifle.card.json\",",
            "  \"assets\": [",
            "    {",
            $"      \"guid\": \"{CardGuid}\",",
            "      \"kind\": \"card\",",
            "      \"id\": \"sample.rifle\",",
            "      \"version\": \"1.0.0\",",
            "      \"loader\": \"builtin.card\",",
            "      \"dependsOn\": [",
            "        {",
            "          \"kind\": \"effect\",",
            "          \"id\": \"pack/sample.pack/fire\",",
            "          \"version\": \"1.*.*\"",
            "        }",
            "      ],",
            "      \"conflictsWith\": []",
            "    },",
            "    {",
            $"      \"guid\": \"{PrefabGuid}\",",
            "      \"kind\": \"trigger\",",
            "      \"id\": \"onPlay\",",
            "      \"version\": \"1.0.0\",",
            "      \"anchor\": \"stableKey.onPlay\",",
            "      \"dependsOn\": [],",
            "      \"conflictsWith\": [",
            "        {",
            "          \"kind\": \"card\",",
            "          \"id\": \"pack/other/pistol\",",
            "          \"version\": \"2.0.0\"",
            "        }",
            "      ]",
            "    }",
            "  ]",
            "}",
        ]);

        Assert.Equal(expected, json.Replace("\r\n", "\n"));
        Assert.DoesNotContain('\r', json);
        Assert.False(json.StartsWith('\uFEFF'));
    }

    [Fact]
    public void 写窄_版本按书写段数原样落盘_读入后按规范形归一()
    {
        var sidecar = new AssetSidecar(
            AssetSidecarJson.CurrentSchemaVersion,
            "sample.pack",
            "cards/a.card.json",
            [new AssetMetadata(Guid.NewGuid(), AssetKind.Card, "a", AssetVersion.Parse("1.2"), null, null, [], [])]);

        var json = AssetSidecarJson.ToJson(sidecar);
        Assert.Contains("\"version\": \"1.2\"", json);
        Assert.DoesNotContain("1.2.0", json);

        var parsed = AssetSidecarJson.Parse(json);
        Assert.Equal(AssetVersion.Parse("1.2"), parsed.Assets[0].Version);
        Assert.Equal(AssetVersion.Parse("1.2.0"), parsed.Assets[0].Version);
    }

    [Fact]
    public void 读宽_注释尾逗号与键名大小写()
    {
        var json = $$"""
        {
          // 读宽：注释与尾逗号
          "SchemaVersion": 1,
          "PACKAGEID": "sample.pack",
          "Source": "cards/a.card.json",
          "Assets": [
            {
              "GUID": "{{Guid.NewGuid()}}",
              "Kind": "CARD",
              "Id": "a",
              "Version": "1",
              "DependsOn": [],
              "ConflictsWith": [],
            },
          ],
        }
        """;

        var parsed = AssetSidecarJson.Parse(json);

        Assert.Single(parsed.Assets);
        Assert.Equal(AssetKind.Card, parsed.Assets[0].Kind);
        Assert.Equal(new AssetVersion(1, 0, 0), parsed.Assets[0].Version);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"packageId":"p","source":"s","assets":[{"kind":"card","id":"a","version":"1"}]}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"p","source":"s","assets":[{"guid":"11111111-1111-4111-8111-111111111111","kind":"nope","id":"a","version":"1"}]}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"p","source":"s","assets":[{"guid":"11111111-1111-4111-8111-111111111111","kind":"card","id":"","version":"1"}]}""")]
    [InlineData("""{"schemaVersion":9,"packageId":"p","source":"s","assets":[]}""")]
    [InlineData("""{"schemaVersion":1,"source":"s","assets":[]}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"p","source":"s"}""")]
    public void 非法sidecar_明确拒绝(string json)
    {
        Assert.Throws<FormatException>(() => AssetSidecarJson.Parse(json));
    }

    [Fact]
    public void 同文件GUID重复声明_拒绝()
    {
        var guid = Guid.NewGuid();
        var json = $$"""
        {
          "schemaVersion": 1,
          "packageId": "p",
          "source": "s",
          "assets": [
            { "guid": "{{guid}}", "kind": "card", "id": "a", "version": "1" },
            { "guid": "{{guid}}", "kind": "card", "id": "b", "version": "1" }
          ]
        }
        """;

        Assert.Throws<FormatException>(() => AssetSidecarJson.Parse(json));
    }

    [Fact]
    public void 空资产列表_往返保真()
    {
        var sidecar = new AssetSidecar(AssetSidecarJson.CurrentSchemaVersion, "p", "cards/empty.card.json", []);
        var json = AssetSidecarJson.ToJson(sidecar);

        Assert.Contains("\"assets\": []", json);

        var parsed = AssetSidecarJson.Parse(json);
        Assert.Empty(parsed.Assets);
        Assert.Equal("cards/empty.card.json", parsed.Source);
    }

    [Fact]
    public void 非法版本原文_写回保真_不静默归一()
    {
        var sidecar = new AssetSidecar(
            AssetSidecarJson.CurrentSchemaVersion,
            "p",
            "cards/a.card.json",
            [new AssetMetadata(Guid.NewGuid(), AssetKind.Card, "a", default, null, null, [], [])
            {
                InvalidVersion = "1.2.3.4.5.6.7.8.9",
            }]);

        var json = AssetSidecarJson.ToJson(sidecar);
        Assert.Contains("\"version\": \"1.2.3.4.5.6.7.8.9\"", json);

        var parsed = Assert.Single(AssetSidecarJson.Parse(json).Assets);
        Assert.Equal("1.2.3.4.5.6.7.8.9", parsed.InvalidVersion);
    }

    [Fact]
    public void 非法版本号_保留原文_交资产级判定()
    {
        var json = """
        {
          "schemaVersion": 1,
          "packageId": "p",
          "source": "s",
          "assets": [
            { "guid": "11111111-1111-4111-8111-111111111111", "kind": "card", "id": "a", "version": "1.2.3.4.5.6.7.8.9" }
          ]
        }
        """;

        var asset = Assert.Single(AssetSidecarJson.Parse(json).Assets);

        // 版本号非法＝**资产级**（该资产非法、不生成），不升为包级结构性拒绝。
        Assert.Equal("1.2.3.4.5.6.7.8.9", asset.InvalidVersion);
        Assert.Equal(default, asset.Version);
    }

    [Fact]
    public void 资产级依赖_复用版本约束匹配()
    {
        var reference = new AssetReference(AssetKind.Effect, "pack/sample.pack/fire", "1.^2.*");

        Assert.True(reference.IsSatisfiedBy(AssetVersion.Parse("1.2.0")));
        Assert.True(reference.IsSatisfiedBy(AssetVersion.Parse("1.7.3")));
        Assert.False(reference.IsSatisfiedBy(AssetVersion.Parse("1.1.9")));
        Assert.False(reference.IsSatisfiedBy(AssetVersion.Parse("2.2.0")));
    }

    [Fact]
    public void 文件名与源文件一对一可逆()
    {
        Assert.Equal("a.card.json.meta.json", AssetSidecarJson.SidecarFileNameFor("a.card.json"));
        Assert.True(AssetSidecarJson.TryGetSourceFileName("a.card.json.meta.json", out var source));
        Assert.Equal("a.card.json", source);

        Assert.False(AssetSidecarJson.TryGetSourceFileName("a.card.json", out _));
        Assert.False(AssetSidecarJson.TryGetSourceFileName(".meta.json", out _));

        var path = Path.Combine("pack", "cards", "a.card.json");
        Assert.Equal(Path.Combine("pack", "cards", "a.card.json.meta.json"), AssetSidecarJson.SidecarPathFor(path));
    }

    [Fact]
    public void 落盘读写_往返一致()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orc-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = AssetSidecarJson.SidecarPathFor(Path.Combine(directory, "a.card.json"));
            AssetSidecarJson.WriteFile(path, SampleSidecar());

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            var parsed = AssetSidecarJson.ReadFile(path);
            Assert.Equal(AssetSidecarJson.ToJson(parsed), AssetSidecarJson.ToJson(SampleSidecar()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
