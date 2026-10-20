using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S2 验收：<see cref="AssetVersion"/> N 段化（D26/R17）——规范形右补 0、段数保留、
/// 比较/相等/哈希一致、递增＝严格大于、默认递增＝尾段 +1。
/// </summary>
public class AssetVersionTests
{
    [Fact]
    public void 比较_缺段补0_第一处不等决定大小()
    {
        Assert.Equal(AssetVersion.Parse("1.2"), new AssetVersion(1, 2, 0));
        Assert.True(AssetVersion.Parse("1.2") < new AssetVersion(1, 2, 1));
        Assert.True(AssetVersion.Parse("1.10") > new AssetVersion(1, 9, 0)); // 逐段数值比较，非字符串比较
        Assert.True(AssetVersion.Parse("1.2.3.4") > new AssetVersion(1, 2, 3));
        Assert.Equal(0, AssetVersion.Parse("2").CompareTo(AssetVersion.Parse("2.0.0.0.0.0.0.0")));
    }

    [Fact]
    public void 规范形相等_且哈希相等_可作字典键()
    {
        var shortForm = AssetVersion.Parse("1.2");
        var padded = AssetVersion.Parse("1.2.0");

        Assert.True(shortForm.Equals(padded));
        Assert.Equal(shortForm.GetHashCode(), padded.GetHashCode());
        Assert.Equal(shortForm, padded);

        var map = new Dictionary<AssetVersion, string> { [shortForm] = "first" };
        Assert.True(map.TryGetValue(padded, out var value));
        Assert.Equal("first", value);
    }

    [Fact]
    public void 三段构造与N段视图_前三段一致_后续段经Segments可见()
    {
        var three = new AssetVersion(1, 2, 3);
        var five = AssetVersion.FromSegments([1, 2, 3, 4, 5]);

        Assert.Equal(three.Major, five.Major);
        Assert.Equal(three.Minor, five.Minor);
        Assert.Equal(three.Patch, five.Patch);
        Assert.Equal(1, five.SegmentAt(0));
        Assert.Equal(2, five.SegmentAt(1));
        Assert.Equal(3, five.SegmentAt(2));
        Assert.Equal(4, five.SegmentAt(3));
        Assert.Equal(5, five.SegmentAt(4));
        Assert.Equal(0, five.SegmentAt(5)); // 缺失段＝0
        Assert.Equal(5, five.SegmentCount);
        Assert.Equal(3, three.SegmentCount);
        Assert.Equal(5, five.Segments.Length);
        Assert.Equal(4, five.Segments[3]);
    }

    [Fact]
    public void ToString_保留书写段数()
    {
        Assert.Equal("1.0.0", new AssetVersion(1, 0, 0).ToString());
        Assert.Equal("1", AssetVersion.Parse("1").ToString());
        Assert.Equal("1.2", AssetVersion.Parse("1.2").ToString());
        Assert.Equal("1.2.3.4.5", AssetVersion.Parse("1.2.3.4.5").ToString());

        // 展示串与规范形分离：字符串不同，版本相等。
        Assert.NotEqual(new AssetVersion(1, 0, 0).ToString(), AssetVersion.Parse("1").ToString());
        Assert.Equal(new AssetVersion(1, 0, 0), AssetVersion.Parse("1"));
    }

    [Fact]
    public void 递增_默认尾段加1_保留段数()
    {
        Assert.Equal("1.3", AssetVersion.Parse("1.2").Increment().ToString());
        Assert.Equal("1.0.1", new AssetVersion(1, 0, 0).Increment().ToString());
        Assert.Equal("1.2.3.4.6", AssetVersion.Parse("1.2.3.4.5").Increment().ToString());
        Assert.True(AssetVersion.Parse("1.2").Increment().IsUpgradeOver(AssetVersion.Parse("1.2")));
    }

    [Theory]
    [InlineData("1.2", "1.2.1", true)]   // 尾段递增
    [InlineData("1.2", "1.3", true)]     // 次版递增
    [InlineData("1.2", "1.2.0.1", true)] // 允许增段
    [InlineData("1.2.3", "1.2", false)]  // 降级拒绝
    [InlineData("1.2", "1.2.0", false)]  // 补 0 后相等 ⇒ 非递增
    [InlineData("1.2", "1.2", false)]    // 相等拒绝
    public void 递增判定_严格大于旧版_降级与补0相等均拒绝(string previous, string candidate, bool expected)
    {
        Assert.Equal(expected, AssetVersion.Parse(candidate).IsUpgradeOver(AssetVersion.Parse(previous)));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3.4.5.6.7.8", true)]
    [InlineData("1.2.3.4.5.6.7.8.9", false)] // 超过 8 段
    [InlineData("1..2", false)]
    [InlineData("-1", false)]
    [InlineData("1.2.a", false)]
    [InlineData("", false)]
    public void 解析_仅接受1到8段非负整数(string text, bool expected)
    {
        Assert.Equal(expected, AssetVersion.TryParse(text, out _));
    }

    [Fact]
    public void 构造时拷贝_外部数组变更不影响版本()
    {
        var source = new[] { 1, 2, 3 };
        var version = AssetVersion.FromSegments(source);

        source[0] = 9;

        Assert.Equal(1, version.Major);
        Assert.Equal("1.2.3", version.ToString());
    }

    [Fact]
    public void 尾段溢出_拒绝递增()
    {
        var version = AssetVersion.FromSegments([1, int.MaxValue]);

        Assert.Throws<InvalidOperationException>(() => version.Increment());
    }

    [Fact]
    public void 段数越界或负值_明确拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetVersion.FromSegments([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetVersion.FromSegments([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetVersion.FromSegments([1, -2]));

        // 三段构造与 FromSegments 同受 R17「非负」校验（两条构造路径不得分叉）。
        Assert.Throws<ArgumentOutOfRangeException>(() => new AssetVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AssetVersion(1, 0, -3));
    }
}
