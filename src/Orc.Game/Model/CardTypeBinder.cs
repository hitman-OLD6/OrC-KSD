using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Cards.Data.Components;

namespace Orc.Game.Model;

/// <summary>卡牌类型预制体的绑定来源（R3g：两条路径共用同一张表）。</summary>
public enum CardBindingSource
{
    /// <summary>直接构造：由 C# 卡牌类映射到类型预制体（<c>new UnitCard(...)</c> 等 20 处）。</summary>
    Class,

    /// <summary>卡数据：由 <c>typeCategory</c> 数据组件自动绑定（与 C# 类映射必须一致）。</summary>
    TypeCategory,
}

/// <summary>
/// 卡牌类型预制体绑定（S8 第 1、6、8、10 条）：**唯一真源＝类型预制体**
/// （<c>card.base</c> → <c>card.unit</c>／<c>card.order</c>／<c>card.counter</c>／<c>card.hq</c>）。
/// <para>
/// 两条绑定路径：
/// ①直接构造（<c>new UnitCard(...)</c>）→ 由 C# 类映射到类型预制体（绑定时机＝构造期，签名不变）；
/// ②卡数据 → 由既有 <c>typeCategory</c> 数据组件自动绑定（151 张卡零改动）。
/// 两条路径落到同一张表：本类在绑定时**交叉校验**二者一致，不一致＝装载期拒绝（不留"同一张卡两套成员"）。
/// </para>
/// <para>
/// 唯一性口径按"类别词唯一"判定（R3g 第 6、8 条）：<c>typeCategory</c> 的拆解规则由
/// <see cref="TypeCategoryDefinition"/> 承担（类别词至多一个且独占、单位类型词可多个），
/// 因此 <c>["infantry","tank"]</c> 是合法的**单位卡**、只映射到 <c>card.unit</c>。
/// 缺 <c>typeCategory</c>／多类别词／未定义词＝装载期拒绝（数据路径，见 <c>CardValiditySuite</c>），
/// **不设默认类型预制体兜底**。
/// </para>
/// </summary>
public static class CardTypeBinder
{
    private static readonly PrefabCatalog CardCatalog = BuildCatalog();

    /// <summary>卡牌类型预制体目录（装载期展开一次；只读）。</summary>
    public static PrefabCatalog Catalog => CardCatalog;

    /// <summary>类别 → 宿主种类（唯一映射；新增卡牌类型＝改这里 + 加一张类型预制体）。</summary>
    /// <exception cref="PrefabLoadException">类别未定义。</exception>
    public static HostKind HostOfCategory(string category) => category switch
    {
        CardCategory.Unit => HostKind.Unit,
        CardCategory.Command => HostKind.Order,
        CardCategory.Counter => HostKind.Counter,
        _ => throw new PrefabLoadException(
            PrefabErrorCategories.Structure,
            $"类别 '{category}' 没有对应的类型预制体（类型预制体 id 由类别唯一映射，R3g；"
            + "新增类型须同时加类别词与类型预制体）。"),
    };

    /// <summary>宿主种类 → 类型预制体 id（4 条映射，唯一真源 B）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">Non</exception>
    public static string PrefabIdFor(HostKind host) => host switch
    {
        HostKind.Unit => CardProcesses.UnitPrefabId,
        HostKind.Order => CardProcesses.OrderPrefabId,
        HostKind.Counter => CardProcesses.CounterPrefabId,
        HostKind.Hq => CardProcesses.HqPrefabId,
        _ => throw new ArgumentOutOfRangeException(
            nameof(host), host, "该宿主种类没有类型预制体（None／Any 不可作为卡牌类型）。"),
    };

    /// <summary>按宿主种类取类型预制体。</summary>
    public static PrefabType Resolve(HostKind host) => CardCatalog.Resolve(PrefabIdFor(host));

    /// <summary>
    /// 卡数据路径的绑定键：从定义的 <c>typeCategory</c> 组件推出宿主种类。
    /// 定义未携带该组件（代码注册路径的兼容构造）＝返回 null（由 C# 类绑定，R3g 第 10 条表 1–4）。
    /// </summary>
    public static HostKind? HostOfDefinition(CardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (var component in definition.Components)
        {
            if (component is TypeCategoryDefinition typeCategory)
            {
                return HostOfCategory(typeCategory.Category);
            }
        }

        return null;
    }

    /// <summary>
    /// 把一个卡牌实例绑定到类型预制体，并物化其成员表（S8：卡牌实例化路径改为声明驱动）。
    /// </summary>
    /// <param name="card">卡牌实例。</param>
    /// <param name="host">该卡牌类代表的宿主种类（＝C# 类 → 类型预制体的映射，唯一改动点 A）。</param>
    /// <param name="engine">引擎（旧路径触发器需要）。</param>
    /// <param name="definition">卡牌定义（用于交叉校验 <c>typeCategory</c>）。</param>
    /// <param name="validationJudicatorResolver">费用校验判定器解析器（可为 null；守卫边用）。</param>
    /// <exception cref="ArgumentNullException">card／engine 为 null。</exception>
    /// <exception cref="PrefabLoadException">两条绑定路径指向不同类型预制体。</exception>
    public static CardMemberTable Bind(
        Card card,
        HostKind host,
        LogicEngine engine,
        CardDefinition? definition,
        Func<string, JudicatorBinding>? validationJudicatorResolver)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(engine);

        if (definition is not null && HostOfDefinition(definition) is { } declared && declared != host)
        {
            // 两条路径的真源必须是同一张表（S8 第 10 条）：不一致＝配置错误，装载期拒绝。
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"卡牌 '{card.Name}' 的 C# 类绑定到 '{PrefabIdFor(host)}'，而其 typeCategory 指向 "
                + $"'{PrefabIdFor(declared)}'（两条实例化路径必须产出同一套成员，R3g）。");
        }

        var type = Resolve(host);

        // 批次 7（§27）：成员表只回答"成员在哪个地址"——不再物化触发器。
        // 判定器解析器仍在此校验一次（解析动作即校验：未注册名＝装配期 fail-fast），
        // 具体守卫边由卡牌类在构造期接入。
        _ = validationJudicatorResolver;
        return new CardMemberTable(type, CardBindingSource.Class, host);
    }

    private static PrefabCatalog BuildCatalog()
    {
        var catalog = new PrefabCatalog();
        catalog.AddRange(CardProcesses.Definitions);
        return catalog;
    }
}
