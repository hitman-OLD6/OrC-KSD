using Orc.Core;
using Orc.Packages.Editing;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S8 验收（R17/D53）：版本规则——测试版本＝正式版追加测试段、转正去段并递增、
/// 默认递增＝尾段 +1、手写须严格大于旧版（降级／补 0 后相等皆拒绝）。
/// </summary>
public class EditorVersionRulesTests
{
    [Fact]
    public void Test_Version_Appends_A_Segment()
    {
        Assert.Equal("1.2.3.1", EditorVersionRules.TestVersionOf(AssetVersion.Parse("1.2.3")).ToString());
        Assert.Equal("1.0.0.1", EditorVersionRules.TestVersionOf(EditorVersionRules.Initial).ToString());
    }

    [Fact]
    public void Test_Version_Cannot_Exceed_Eight_Segments()
    {
        var exception = Assert.Throws<AssetEditorException>(
            () => EditorVersionRules.TestVersionOf(AssetVersion.Parse("1.2.3.4.5.6.7.8")));

        Assert.Equal(AssetEditorErrorCode.InvalidVersion, exception.Code);
    }

    [Fact]
    public void Promote_Drops_Test_Segment_And_Increments()
    {
        Assert.Equal(
            "1.2.4",
            EditorVersionRules.Promote(AssetVersion.Parse("1.2.3.5"), AssetVersion.Parse("1.2.3")).ToString());

        Assert.Equal(
            "1.2.3",
            EditorVersionRules.Promote(AssetVersion.Parse("1.2.2.9"), AssetVersion.Parse("1.2.2")).ToString());
    }

    [Fact]
    public void Promote_Leaves_Unchanged_Or_New_Assets_Alone()
    {
        var formal = AssetVersion.Parse("1.0.0");

        Assert.Equal("1.0.0", EditorVersionRules.Promote(formal, formal).ToString());   // 未改动
        Assert.Equal("1.0.0", EditorVersionRules.Promote(formal, baseline: null).ToString()); // 本会话新建（R12）
    }

    [Fact]
    public void Default_Next_Increments_The_Tail()
    {
        Assert.Equal("1.3", EditorVersionRules.Next(AssetVersion.Parse("1.2")).ToString());
        Assert.Equal("1.0.1", EditorVersionRules.Next(AssetVersion.Parse("1.0.0")).ToString());
        Assert.Equal("1.0.0.2", EditorVersionRules.Next(AssetVersion.Parse("1.0.0.1")).ToString());
    }

    [Theory]
    [InlineData("1.0.0")]   // 降级
    [InlineData("1.0.0.1")] // 相等
    public void Manual_Version_Must_Be_Strictly_Greater(string candidate)
    {
        var exception = Assert.Throws<AssetEditorException>(
            () => EditorVersionRules.ParseIncreasing(candidate, AssetVersion.Parse("1.0.0.1")));

        Assert.Equal(AssetEditorErrorCode.VersionNotIncreasing, exception.Code);
    }

    [Fact]
    public void Zero_Padding_Equality_Is_Rejected()
    {
        var exception = Assert.Throws<AssetEditorException>(
            () => EditorVersionRules.ParseIncreasing("1.2.0", AssetVersion.Parse("1.2")));

        Assert.Equal(AssetEditorErrorCode.VersionNotIncreasing, exception.Code);
    }

    [Theory]
    [InlineData("1.2", "1.2.1")] // 增段：合法
    [InlineData("1.2", "1.3")]   // 尾段 +1：合法
    [InlineData("1.2.3", "1.2.4")]
    public void Manual_Version_Accepts_Real_Upgrades(string current, string candidate)
    {
        Assert.Equal(
            candidate,
            EditorVersionRules.ParseIncreasing(candidate, AssetVersion.Parse(current)).ToString());
    }

    [Fact]
    public void Illegal_Format_Is_Rejected()
    {
        foreach (var text in new[] { "1.2.a", "1..2", "1.2.3.4.5.6.7.8.9", "-1" })
        {
            var exception = Assert.Throws<AssetEditorException>(
                () => EditorVersionRules.ParseIncreasing(text, EditorVersionRules.Initial));
            Assert.Equal(AssetEditorErrorCode.InvalidVersion, exception.Code);
        }
    }
}
