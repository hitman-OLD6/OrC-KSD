using Orc.Core.Model;

namespace Orc.Game.Model;

/// <summary>
/// 卡牌实例的成员表（S8 第 1、9 条）：**成员清单由类型预制体决定**，卡牌类只按名取用。
/// <para>
/// 这是"声明驱动"的落点：构造器不再决定"有哪些触发器"，而是
/// <c>Members.AddressOf(CardProcesses.Deploy)</c>——预制体没声明该成员＝构造期明确失败
/// （而不是悄悄少了某个成员）。新增成员＝往类型预制体加一行，四个卡牌类零改动。
/// </para>
/// <para>
/// **批次 7（§27）**：本类不再物化任何执行体。旧实现把每个声明成员物化成一个
/// <c>Trigger&lt;TView&gt;</c>（经 `CardLegacyTriggerFactory`），那是旧执行面的桥；
/// 现在成员表只回答"这个成员在哪个地址上"——执行一律走宿主编排实体的成员
/// （<c>entity.InvokeAsync(member, ctx)</c>：自有边先跑、外来挂载边随后）。
/// </para>
/// </summary>
public sealed class CardMemberTable
{
    internal CardMemberTable(PrefabType type, CardBindingSource source, HostKind host)
    {
        Type = type;
        Source = source;
        Host = host;
    }

    /// <summary>绑定的类型预制体（＝类）。</summary>
    public PrefabType Type { get; }

    /// <summary>绑定的类型预制体 id。</summary>
    public string PrefabId => Type.Id;

    /// <summary>绑定来源（C# 类映射 / <c>typeCategory</c>）。</summary>
    public CardBindingSource Source { get; }

    /// <summary>宿主种类（unit／order／counter／hq）。</summary>
    public HostKind Host { get; }

    /// <summary>声明序的成员名（来自类型预制体）。</summary>
    public IReadOnlyList<string> MemberNames => Type.MemberNames;

    /// <summary>声明序的成员定义。</summary>
    public IReadOnlyList<MemberDefinition> Members => Type.Members;

    /// <summary>是否声明了某成员。</summary>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Type.HasMember(name);
    }

    /// <summary>取成员声明（未声明＝明确失败：卡牌类引用了一个预制体没有的成员）。</summary>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    /// <exception cref="KeyNotFoundException">成员未在类型预制体中声明。</exception>
    public MemberDefinition Member(string name)
        => Type.FindMember(name) ?? throw new KeyNotFoundException(
            $"类型预制体 '{Type.Id}' 没有声明成员 '{name}'（卡牌类引用了未声明的成员——声明驱动，S8 第 9 条）。");

    /// <summary>
    /// 成员的**宿主地址**（<c>host#&lt;成员名&gt;</c>）：卡与 HQ 共用同名成员（如 <c>targeting</c>）时，
    /// 按当前宿主实例天然消歧——地址是登记与审查的唯一身份，不再有"显示名"这一层。
    /// </summary>
    /// <exception cref="ArgumentNullException">name 为 null。</exception>
    /// <exception cref="KeyNotFoundException">成员未在类型预制体中声明。</exception>
    public MemberAddress AddressOf(string name)
        => MemberAddress.Create(AddressScope.Host, Member(name).Name);
}
