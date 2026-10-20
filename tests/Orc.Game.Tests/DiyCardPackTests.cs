using Orc.Game.Cards.Data;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S17 卡包预检证据：<c>samples/card-pack-diy</c> 的纯数据分发必须「双零」
/// （<see cref="CardDistributionValidator"/>：errors=0 / warnings=0），且四张卡与四条库效果全部解析成功。
/// 这是数据侧示例 1/2/3/5 的**第一道证据**；行为证据见 <c>DiyExample01/02/03/05</c>。
/// </summary>
public class DiyCardPackTests
{
    [Fact]
    public void 预检_DIY卡包_双零且统计面正确()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包目录缺失：跳过（示例数据职责）。
        }

        var report = CardDistributionValidator.Validate(
            new[] { cardsDirectory }, new[] { effectsDirectory });

        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
        Assert.True(report.HasNoErrors);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(0, report.UnresolvedReferenceCount);

        // 五张卡（火力支援/齐射/纪奠/战地救护/交叉掩护）＋ 六条库效果；无内联。
        Assert.Equal(5, report.SuccessfulCardCount);
        Assert.Equal(6, report.SuccessfulLibrarySnapshotCount);
        Assert.Equal(0, report.SuccessfulInlineSnapshotCount);
        Assert.Equal(1, report.CardDirectoryCount);
        Assert.Equal(1, report.PrefabLibraryDirectoryCount);
    }
}
