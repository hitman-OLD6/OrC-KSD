using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 阵线**有序单位列表**模型锚点（原槽位模型已退场）：
/// 元素顺序即位置、容量按元素数（HQ 计入）、插入不居中重排、可插入位置＝N+1 个临时空隙（含首尾）、满员＝无空隙。
/// </summary>
public class SlotModelTests
{
    private static UnitCard CreateUnit(LogicEngine engine, string name = "单位")
        => new(engine, new CardDefinition(name, 1, 2, 3, 4, faction: Faction.Germany, rarity: Rarity.Standard));

    [Fact]
    public async Task Empty_Line_Offers_Single_Gap_And_Zero_Elements()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var frontLine = match.Battlefield.FrontLine;
        Assert.Equal(5, frontLine.Capacity);
        Assert.Equal(0, frontLine.Size);
        Assert.Single(frontLine.GetInsertionGaps());
        Assert.Equal(0, frontLine.GetInsertionGaps()[0].Ordinal);
    }

    [Fact]
    public async Task Hq_Starts_At_Leftmost_Position()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var lineA = match.Battlefield.PlayerASupportLine;
        var lineB = match.Battlefield.PlayerBSupportLine;

        Assert.Equal(1, lineA.Size);
        Assert.Same(match.Players[0].Hq, lineA[0]);
        Assert.Same(match.Players[1].Hq, lineB[0]);
        Assert.Equal(0, Battlefield.IndexOfHq(lineA));
        Assert.Equal(0, Battlefield.IndexOfHq(lineB));
    }

    [Fact]
    public async Task Insert_Keeps_Order_Without_Centering()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var line = match.Battlefield.PlayerASupportLine;
        var unit = CreateUnit(match.Engine, "甲");

        // 插到 HQ 右侧（位置 1）——HQ 保持在最左，不被居中重排。
        Assert.True(line.TryInsertAt(1, unit));
        Assert.Equal(2, line.Size);
        Assert.Same(match.Players[0].Hq, line[0]);
        Assert.Same(unit, line[1]);
        Assert.Equal(1, line.IndexOf(unit));

        // 再插到最左——新元素占据 0，HQ 与甲顺移到 1/2。
        var left = CreateUnit(match.Engine, "乙");
        Assert.True(line.TryInsertAt(0, left));
        Assert.Same(left, line[0]);
        Assert.Same(match.Players[0].Hq, line[1]);
        Assert.Same(unit, line[2]);
    }

    [Fact]
    public async Task Hq_Leftmost_With_Four_Units_Is_Full_And_Has_No_Gap()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var line = match.Battlefield.PlayerASupportLine;
        for (var i = 0; i < 4; i++)
        {
            Assert.True(line.TryInsertAt(line.Size, CreateUnit(match.Engine, $"单位{i}")));
        }

        Assert.Equal(5, line.Size);
        Assert.Equal(0, Battlefield.IndexOfHq(line)); // HQ 仍在最左
        Assert.True(line.IsFull);
        Assert.Empty(line.GetInsertionGaps());
    }

    [Fact]
    public async Task Old_Gap_Becomes_Stale_After_Insert()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var line = match.Battlefield.PlayerASupportLine;
        var gap = line.GetInsertionGaps()[1];
        Assert.True(gap.IsCurrent);

        Assert.True(line.TryInsertAt(1, CreateUnit(match.Engine, "先入")));
        Assert.False(gap.IsCurrent);
        Assert.False(line.TryInsert(gap, CreateUnit(match.Engine, "后入"))); // 旧候选被拒绝
    }

    [Fact]
    public async Task Gaps_Are_Reused_While_Snapshot_Unchanged()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var line = match.Battlefield.PlayerASupportLine;
        var first = line.GetInsertionGaps();
        var second = line.GetInsertionGaps();

        Assert.Equal(first.Count, second.Count);
        Assert.Same(first[0], second[0]); // 占用未变＝复用同一批实例
    }

    [Fact]
    public async Task BoardPosition_Reports_Occupant_And_Index()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var line = match.Battlefield.PlayerASupportLine;
        var position = new BoardPosition(line, 0);

        Assert.Same(match.Players[0].Hq, position.Occupant);
        Assert.Equal(0, position.Index);
        Assert.NotNull(position.Ref);
    }

    [Fact]
    public void Invalid_Capacity_And_Index_Are_Explicit_Errors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BattleLine(0));

        var line = new BattleLine(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = line[0]); // 空线：任何位置越界
        Assert.False(line.TryInsertAt(1, CreateUnit(new LogicEngine()))); // 序号越界＝false、零变更
        Assert.Equal(0, line.Size);
    }
}
