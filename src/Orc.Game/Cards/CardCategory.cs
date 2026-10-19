namespace Orc.Game.Cards;

/// <summary>
/// 卡牌类别（三大类卡牌体系在定义层的区分：单位 / 指令 / 反制）——**字符串常量类**
/// （资产化 批次 4：由 <c>enum</c> 改为 string 常量；真源＝资产服务，本类＝内置项可读符号）。
/// 定义层经 <see cref="CardDefinition.Category"/> 承载；实例化经 <see cref="CardLibrary.Instantiate"/>
/// 产出对应基类实例（<see cref="UnitCard"/> / <see cref="OrderCard"/> / <see cref="CounterCard"/>）。
/// 未定义值在定义期（构造）被拒绝（配置错误不吞）。
/// </summary>
public static class CardCategory
{
    /// <summary>单位：部署入战场、参与战斗。</summary>
    public const string Unit = "unit";

    /// <summary>指令：打出即生效的主动效果载体。</summary>
    public const string Command = "command";

    /// <summary>反制：激活/取消两态、使用反制的触发器。</summary>
    public const string Counter = "counter";

    /// <summary>内置类别标识清单（登记序）。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Unit, Command, Counter };

    /// <summary>是否为已定义类别标识（null/空白＝false、不抛错）。</summary>
    public static bool IsDefined(string? value)
        => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>归一到规范标识（大小写不敏感——卡数据用 <c>order</c>/<c>countermeasure</c> 等词，另经映射；未定义＝false）。</summary>
    public static bool TryNormalize(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var lowered = value.Trim().ToLowerInvariant();
        if (!All.Contains(lowered, StringComparer.Ordinal))
        {
            return false;
        }

        canonical = lowered;
        return true;
    }
}
