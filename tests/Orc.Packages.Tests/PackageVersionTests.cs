using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S4/S20 验收：包版本＝**内容指纹**（F1）——
/// <see cref="PackageVersion.ComputeContent"/> 对同一内容恒得同一指纹、内容变化必改指纹、不含时间戳；
/// 旧语义保留为 <see cref="PackageVersion.ComputeLayout"/>（只哈希路径列表）；
/// 临时包加 <c>-tmp</c>、转正去掉；不参与任何身份/解析。
/// </summary>
public class PackageVersionTests
{
    private static readonly Guid PackageGuid = PackageGuidOf("sample.pack");

    // ── 布局指纹（旧语义，仅供布局审计） ──────────────────────────────────────

    [Fact]
    public void 布局指纹_同一内容同一指纹()
    {
        var first = PackageVersion.ComputeLayout("sample.pack", ["cards/a.card.json.meta.json"]);
        var second = PackageVersion.ComputeLayout("sample.pack", ["cards/a.card.json.meta.json"]);

        Assert.Equal(first, second);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(32, first.Fingerprint.Length);
        Assert.All(first.Fingerprint, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void 布局指纹_元数据顺序不影响_内容不同则不同()
    {
        var a = PackageVersion.ComputeLayout("sample.pack", ["a.meta.json", "b.meta.json"]);
        var b = PackageVersion.ComputeLayout("sample.pack", ["b.meta.json", "a.meta.json"]);
        var c = PackageVersion.ComputeLayout("sample.pack", ["a.meta.json", "c.meta.json"]);
        var otherPackage = PackageVersion.ComputeLayout("sample.other", ["a.meta.json", "b.meta.json"]);

        Assert.Equal(a, b);          // 同一内容（顺序无关）＝同指纹
        Assert.NotEqual(a, c);       // 内容异构＝不同指纹
        Assert.NotEqual(a, otherPackage); // 包 ID 参与指纹
    }

    // ── 内容指纹（F1） ──────────────────────────────────────────────────────

    [Fact]
    public void 内容指纹_同一内容恒得同一指纹且不含时间戳()
    {
        var sidecar = Sidecar("cards/a.card.json", Asset("a"));
        var first = Content("sample.pack", ("cards/a.card.json.meta.json", sidecar));
        Thread.Sleep(5);
        var second = Content("sample.pack", ("cards/a.card.json.meta.json", sidecar));

        Assert.Equal(first, second);
        Assert.Equal(32, first.Fingerprint.Length);
        Assert.All(first.Fingerprint, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        Assert.DoesNotContain(DateTime.UtcNow.Year.ToString(), first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 内容指纹_同路径改资产版本_指纹变化()
    {
        var v1 = Content("sample.pack", ("cards/a.card.json.meta.json", Sidecar("cards/a.card.json", Asset("a", "1.0.0"))));
        var v2 = Content("sample.pack", ("cards/a.card.json.meta.json", Sidecar("cards/a.card.json", Asset("a", "1.1.0"))));

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void 内容指纹_同内容不同书写顺序_指纹相同()
    {
        var a = Asset("a");
        var b = Asset("b", "1.1.0");
        var ordered = Sidecar("cards/a.card.json", a, b);

        // 读宽：键序打乱、注释、尾逗号、assets 顺序颠倒。
        var wide = $$"""
            {
              // 注释与键序都不影响内容语义
              "source": "cards/a.card.json",
              "packageId": "sample.pack",
              "schemaVersion": 1,
              "assets": [
                {
                  "id": "b",
                  "kind": "card",
                  "guid": "{{b.Guid}}",
                  "version": "1.1.0",
                  "dependsOn": [],
                  "conflictsWith": [],
                },
                {
                  "version": "1.0.0",
                  "guid": "{{a.Guid}}",
                  "kind": "card",
                  "id": "a",
                  "conflictsWith": [],
                  "dependsOn": []
                },
              ],
            }
            """;

        Assert.Equal(
            Content("sample.pack", ("cards/a.card.json.meta.json", ordered)),
            Content("sample.pack", ("cards/a.card.json.meta.json", AssetSidecarJson.Parse(wide))));
    }

    [Fact]
    public void 内容指纹_依赖冲突锚与加载器属于内容()
    {
        var baseline = Content("sample.pack",
            ("effects/x.prefab.json.meta.json", Sidecar("effects/x.prefab.json", Asset("x", kind: AssetKind.Effect))));

        var dependsOn = Content("sample.pack",
            ("effects/x.prefab.json.meta.json", Sidecar("effects/x.prefab.json",
                Asset("x", kind: AssetKind.Effect,
                    dependsOn: [new AssetReference(AssetKind.Card, "pack/sample.pack/a", "1.*.*")]))));
        var conflictsWith = Content("sample.pack",
            ("effects/x.prefab.json.meta.json", Sidecar("effects/x.prefab.json",
                Asset("x", kind: AssetKind.Effect,
                    conflictsWith: [new AssetReference(AssetKind.Card, "pack/sample.pack/a", "1.*.*")]))));
        var anchor = Content("sample.pack",
            ("effects/x.prefab.json.meta.json", Sidecar("effects/x.prefab.json",
                Asset("x", kind: AssetKind.Effect, anchor: "onPlay"))));
        var loader = Content("sample.pack",
            ("effects/x.prefab.json.meta.json", Sidecar("effects/x.prefab.json",
                Asset("x", kind: AssetKind.Effect, loader: "builtin.effect"))));

        Assert.NotEqual(baseline, dependsOn);
        Assert.NotEqual(baseline, conflictsWith);
        Assert.NotEqual(baseline, anchor);
        Assert.NotEqual(baseline, loader);
    }

    [Fact]
    public void 内容指纹_排除历史归档与插件清单镜像()
    {
        var current = Sidecar("cards/a.card.json", Asset("a"));
        var without = Content("sample.pack", ("cards/a.card.json.meta.json", current));

        var withExcluded = Content(
            "sample.pack",
            ("cards/a.card.json.meta.json", current),
            ("versions/1.0.0/cards/a.card.json.meta.json", Sidecar("versions/1.0.0/cards/a.card.json", Asset("a", "9.9.9"))),
            (PluginAssetRegistrar.ManifestFileName, Sidecar("cards/a.card.json", Asset("a", "9.9.9"))));

        Assert.Equal(without, withExcluded);
    }

    [Fact]
    public void 内容指纹_packageId不一致或空白_抛ArgumentException()
    {
        var sidecar = Sidecar("cards/a.card.json", Asset("a"));

        Assert.Throws<ArgumentException>(() => Content("other.pack", ("cards/a.card.json.meta.json", sidecar)));
        Assert.Throws<ArgumentException>(() => Content("   ", ("cards/a.card.json.meta.json", sidecar)));
    }

    [Fact]
    public void 内容指纹_空assets的sidecar变化同样改指纹()
    {
        var empty = Sidecar("cards/a.card.json");
        var declared = Sidecar("cards/a.card.json", Asset("a"));

        Assert.NotEqual(
            Content("sample.pack", ("cards/a.card.json.meta.json", empty)),
            Content("sample.pack", ("cards/a.card.json.meta.json", declared)));
    }

    [Fact]
    public void 内容指纹_非约定目录的sidecar也参与()
    {
        var baseline = Content("sample.pack", ("cards/a.card.json.meta.json", Sidecar("cards/a.card.json", Asset("a"))));
        var extra = Content(
            "sample.pack",
            ("cards/a.card.json.meta.json", Sidecar("cards/a.card.json", Asset("a"))),
            ("units/x.prefab.json.meta.json", Sidecar("units/x.prefab.json", Asset("x", kind: AssetKind.Effect))));

        Assert.NotEqual(baseline, extra); // 指纹＝包内容（注册面按目录约定另算）
    }

    [Fact]
    public void 临时包带tmp_转正去掉()
    {
        var formal = Content("sample.pack", ("cards/a.card.json.meta.json", Sidecar("cards/a.card.json", Asset("a"))));
        var temporary = formal.AsTemporary();

        Assert.False(formal.IsTemporary);
        Assert.True(temporary.IsTemporary);
        Assert.EndsWith("-tmp", temporary.ToString());
        Assert.Equal(formal.Fingerprint, temporary.Fingerprint);
        Assert.Equal(formal, temporary.AsPromoted());
        Assert.Equal(temporary, formal.AsTemporary());
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("abc123-tmp", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("-tmp", false)]
    public void 解析_读宽但拒绝非法串(string text, bool expected)
    {
        Assert.Equal(expected, PackageVersion.TryParse(text, out _));
    }

    [Fact]
    public void 样例包_重算内容指纹与manifest一致()
    {
        var samples = Path.Combine(FindRepoRoot(), "samples");
        var packages = Directory.EnumerateDirectories(samples)
            .Where(directory => File.Exists(Path.Combine(directory, "manifest.json")))
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(packages);
        foreach (var package in packages)
        {
            var manifest = PackageManifest.Parse(File.ReadAllText(Path.Combine(package, "manifest.json")));
            Assert.Equal(
                AssetLoadTestKit.RecomputeContent(package, manifest.Id),
                manifest.Version);
        }
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static PackageVersion Content(
        string packageId,
        params (string RelativePath, AssetSidecar Sidecar)[] sidecars)
        => PackageVersion.ComputeContent(packageId, sidecars);

    private static AssetSidecar Sidecar(string source, params AssetMetadata[] assets)
        => new(AssetSidecarJson.CurrentSchemaVersion, "sample.pack", source, assets);

    private static AssetMetadata Asset(
        string id,
        string version = "1.0.0",
        AssetKind kind = AssetKind.Card,
        string? anchor = null,
        string? loader = null,
        AssetReference[]? dependsOn = null,
        AssetReference[]? conflictsWith = null)
    {
        var parsed = AssetVersion.Parse(version);
        return new AssetMetadata(
            AssetIdentity.DeriveAssetGuid(PackageGuid, kind, id, parsed),
            kind,
            id,
            parsed,
            anchor,
            loader,
            dependsOn ?? [],
            conflictsWith ?? []);
    }

    private static Guid PackageGuidOf(string packageId)
        => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(packageId)).AsSpan(0, 16));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
