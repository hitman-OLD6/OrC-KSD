using Orc.Core;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S4 验收：lock 读写——两文件（正式／测试）格式与键序稳定、读宽写窄、缺失＝null、非法拒绝。
/// </summary>
public class AssetLockTests
{
    private static readonly Guid CardGuid = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid EffectGuid = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static AssetLock SampleLock() => AssetLock.Create(
        "sample.pack",
        AssetDomain.Formal,
        [
            new AssetLockEntry(EffectGuid, AssetKind.Effect, "pack/sample.pack/fire", new AssetVersion(1, 0, 0)),
            new AssetLockEntry(CardGuid, AssetKind.Card, "pack/sample.pack/rifle", new AssetVersion(1, 2, 0)),
        ]);

    [Fact]
    public void 两文件按域分离()
    {
        Assert.Equal("lock.formal.json", AssetLockJson.FileNameFor(AssetDomain.Formal));
        Assert.Equal("lock.test.json", AssetLockJson.FileNameFor(AssetDomain.Test));
        Assert.Equal("lock.formal.json", SampleLock().FileName);
        Assert.Equal("lock.test.json", (SampleLock() with { Domain = AssetDomain.Test }).FileName);
    }

    [Fact]
    public void 写窄_固定键序与二空格与LF()
    {
        var json = AssetLockJson.ToJson(SampleLock());

        var expected = string.Join("\n",
        [
            "{",
            "  \"schemaVersion\": 1,",
            "  \"packageId\": \"sample.pack\",",
            "  \"domain\": \"formal\",",
            "  \"assets\": [",
            "    {",
            $"      \"guid\": \"{CardGuid}\",",
            "      \"kind\": \"card\",",
            "      \"id\": \"pack/sample.pack/rifle\",",
            "      \"version\": \"1.2.0\"",
            "    },",
            "    {",
            $"      \"guid\": \"{EffectGuid}\",",
            "      \"kind\": \"effect\",",
            "      \"id\": \"pack/sample.pack/fire\",",
            "      \"version\": \"1.0.0\"",
            "    }",
            "  ]",
            "}",
        ]);

        Assert.Equal(expected, json.Replace("\r\n", "\n"));
    }

    [Fact]
    public void 读写往返_语义保真()
    {
        var parsed = AssetLockJson.Parse(AssetLockJson.ToJson(SampleLock()));

        Assert.Equal(AssetDomain.Formal, parsed.Domain);
        Assert.Equal("sample.pack", parsed.PackageId);
        Assert.Equal(2, parsed.Assets.Count);
        Assert.Equal(new AssetLockEntry(CardGuid, AssetKind.Card, "pack/sample.pack/rifle", new AssetVersion(1, 2, 0)),
            parsed.Assets[0]);
        Assert.Equal(AssetLockJson.ToJson(SampleLock()), AssetLockJson.ToJson(parsed));
    }

    [Fact]
    public void 包内缺失_返回null()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(AssetLockJson.ReadPackage(root, AssetDomain.Formal));

            AssetLockJson.WriteFile(AssetLockJson.PathFor(root, AssetDomain.Test), SampleLock() with { Domain = AssetDomain.Test });

            Assert.NotNull(AssetLockJson.ReadPackage(root, AssetDomain.Test));
            Assert.Null(AssetLockJson.ReadPackage(root, AssetDomain.Formal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"packageId":"p","domain":"nope","assets":[]}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"p","domain":"formal","assets":[{"guid":"x","kind":"card","id":"pack/p/a","version":"1"}]}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"p","domain":"formal","assets":[{"guid":"11111111-1111-4111-8111-111111111111","kind":"nope","id":"pack/p/a","version":"1"}]}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"p","domain":"formal","assets":[{"guid":"11111111-1111-4111-8111-111111111111","kind":"card","id":"a","version":"1"}]}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"p","domain":"formal"}""")]
    public void 非法lock_明确拒绝(string json)
    {
        Assert.Throws<FormatException>(() => AssetLockJson.Parse(json));
    }
}
