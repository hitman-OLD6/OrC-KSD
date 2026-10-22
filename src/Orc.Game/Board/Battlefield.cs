using Orc.Cards;
using Orc.Game.Players;

namespace Orc.Game.Board;

/// <summary>
/// 战场（KARDS 模仿）：三条阵线——玩家A 支援线 / 前线（中立共享）/ 玩家B 支援线，各为**有序单位列表**
/// （<see cref="BattleLine"/>；元素＝HQ 与单位，顺序即位置；容量 5，HQ 计入）。
/// <para>归属固定（对象稳定、不随回合滚动；"我方/敌方"为调用方视图概念，战场不做滚动）；前线不属于任何一方。</para>
/// <para>HQ 初始入列于各自支援线**第 0 位**（此后插入不居中重排——HQ 可被新单位挤到任意位置，也可在最左）。</para>
/// </summary>
public sealed class Battlefield
{
    /// <summary>创建战场（构造期即含 HQ：玩家A / 玩家B 各自支援线首位）。</summary>
    /// <exception cref="ArgumentNullException">playerA 或 playerB 为 null。</exception>
    public Battlefield(Player playerA, Player playerB)
    {
        ArgumentNullException.ThrowIfNull(playerA);
        ArgumentNullException.ThrowIfNull(playerB);

        PlayerASupportLine = new BattleLine(5, "玩家A支援线");
        FrontLine = new BattleLine(5, "前线");
        PlayerBSupportLine = new BattleLine(5, "玩家B支援线");

        PlayerASupportLine.AppendInitial(playerA.Hq);
        playerA.Hq.AttachToLine(PlayerASupportLine);
        PlayerBSupportLine.AppendInitial(playerB.Hq);
        playerB.Hq.AttachToLine(PlayerBSupportLine);
    }

    /// <summary>阵线中 HQ 的位置序号；未找到＝-1（HQ 的位置以线为准，调用方不假设它在某一格）。</summary>
    /// <exception cref="ArgumentNullException">line 为 null。</exception>
    public static int IndexOfHq(BattleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        for (var i = 0; i < line.Size; i++)
        {
            if (line[i] is Hq)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>玩家A 的支援线（固定归属）。</summary>
    public BattleLine PlayerASupportLine { get; }

    /// <summary>前线（中立共享）。</summary>
    public BattleLine FrontLine { get; }

    /// <summary>玩家B 的支援线（固定归属）。</summary>
    public BattleLine PlayerBSupportLine { get; }

    /// <summary>玩家A 支援线容量（5）。</summary>
    public int PlayerASupportLineCapacity => PlayerASupportLine.Capacity;

    /// <summary>前线容量（5）。</summary>
    public int FrontLineCapacity => FrontLine.Capacity;

    /// <summary>玩家B 支援线容量（5）。</summary>
    public int PlayerBSupportLineCapacity => PlayerBSupportLine.Capacity;

    /// <summary>按玩家索引取支援线（0＝玩家A 线、1＝玩家B 线）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">playerIndex 非 0/1。</exception>
    public BattleLine GetSupportLine(int playerIndex) => playerIndex switch
    {
        0 => PlayerASupportLine,
        1 => PlayerBSupportLine,
        _ => throw new ArgumentOutOfRangeException(nameof(playerIndex), playerIndex, "玩家索引须为 0（玩家A）或 1（玩家B）。"),
    };

    /// <summary>按玩家取支援线（便捷转发到索引版）。</summary>
    /// <exception cref="ArgumentNullException">player 为 null。</exception>
    public BattleLine GetSupportLine(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return GetSupportLine(player.Index);
    }

    /// <summary>按线身份解析"归属方"（支援线→玩家；前线＝null＝中立）。</summary>
    /// <exception cref="ArgumentNullException">line / playerA / playerB 为 null。</exception>
    public Player? OwnerOf(BattleLine line, Player playerA, Player playerB)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(playerA);
        ArgumentNullException.ThrowIfNull(playerB);
        if (ReferenceEquals(line, PlayerASupportLine))
        {
            return playerA;
        }

        return ReferenceEquals(line, PlayerBSupportLine) ? playerB : null;
    }

    /// <summary>元素所在线（三线查找；不在任何线＝null）。</summary>
    /// <exception cref="ArgumentNullException">card 为 null。</exception>
    public BattleLine? LineOf(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (PlayerASupportLine.Contains(card))
        {
            return PlayerASupportLine;
        }

        if (FrontLine.Contains(card))
        {
            return FrontLine;
        }

        return PlayerBSupportLine.Contains(card) ? PlayerBSupportLine : null;
    }

    /// <summary>线上首个满足谓词的元素（无＝null）。</summary>
    /// <exception cref="ArgumentNullException">line / predicate 为 null。</exception>
    public static Card? FirstOrDefault(BattleLine line, Func<Card, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(predicate);
        foreach (var member in line.Members)
        {
            if (predicate(member))
            {
                return member;
            }
        }

        return null;
    }
}
