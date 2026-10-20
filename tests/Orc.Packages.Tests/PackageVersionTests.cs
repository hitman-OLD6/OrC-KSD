using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S4 验收：包版本＝**内容指纹串**（<c>hash(包 ID ＋ 包内元数据)</c>）——同一内容同指纹、不同内容不同指纹、
/// **不含时间戳**；临时包加 <c>-tmp</c>、转正去掉；不参与任何身份/解析。
/// </summary>
public class PackageVersionTests
{
    [Fact]
    public void 同一内容_同一指纹()
    {
        var first = PackageVersion.Compute("sample.pack", ["cards/a.card.json.meta.json"]);
        var second = PackageVersion.Compute("sample.pack", ["cards/a.card.json.meta.json"]);

        Assert.Equal(first, second);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(32, first.Fingerprint.Length);
        Assert.All(first.Fingerprint, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void 元数据顺序不影响指纹_内容不同则指纹不同()
    {
        var a = PackageVersion.Compute("sample.pack", ["a.meta.json", "b.meta.json"]);
        var b = PackageVersion.Compute("sample.pack", ["b.meta.json", "a.meta.json"]);
        var c = PackageVersion.Compute("sample.pack", ["a.meta.json", "c.meta.json"]);
        var otherPackage = PackageVersion.Compute("sample.other", ["a.meta.json", "b.meta.json"]);

        Assert.Equal(a, b);          // 同一内容（顺序无关）＝同指纹
        Assert.NotEqual(a, c);       // 内容异构＝不同指纹
        Assert.NotEqual(a, otherPackage); // 包 ID 参与指纹
    }

    [Fact]
    public void 不含时间戳_跨调用稳定()
    {
        var first = PackageVersion.Compute("sample.pack", ["a.meta.json"]);
        Thread.Sleep(5);
        var second = PackageVersion.Compute("sample.pack", ["a.meta.json"]);

        Assert.Equal(first, second);
        Assert.DoesNotContain(DateTime.UtcNow.Year.ToString(), first.Text);
    }

    [Fact]
    public void 临时包带tmp_转正去掉()
    {
        var formal = PackageVersion.Compute("sample.pack", []);
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
}
