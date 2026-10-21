using Orc.Core;

namespace Orc.Game.Targeting;

/// <summary>选择器交互模式（决定"空提交"语义：拖拽空＝取消、点选空提交＝非法选择）。</summary>
public enum SelectorInteractionMode
{
    /// <summary>点选（有提交门控；空提交＝非法选择）。</summary>
    Click,

    /// <summary>拖拽（松手/离开屏幕无目标＝取消）。</summary>
    Drag,
}

/// <summary>
/// 选择器呈现数据（交付前端）：类型名（前端据以选视觉）、交互模式、候选、数量约束、参数。
/// 纯呈现数据，不参与判定（判定归后端选择器实例）。
/// </summary>
public sealed class SelectorPresentation
{
    internal SelectorPresentation(
        string selectorName,
        SelectorInteractionMode mode,
        IReadOnlyList<Ref<Entity>> candidates,
        IReadOnlyList<string>? identifiers,
        int min,
        int max,
        object? parameter,
        bool hasParameter)
    {
        SelectorName = selectorName;
        Mode = mode;
        Candidates = candidates;
        Identifiers = identifiers;
        Min = min;
        Max = max;
        Parameter = parameter;
        HasParameter = hasParameter;
    }

    /// <summary>
    /// 选靶归属座位（多人支持 S6；由 <see cref="SelectorParameter.Owner"/> 注入）。
    /// <c>null</c>＝未指定归属（宿主回落当前动作发起者）。
    /// </summary>
    public int? OwnerSeat { get; internal set; }

    /// <summary>
    /// 本次选靶所属的动作类别（R8 的 <c>pending.action</c>；取值见 <see cref="SelectorActions"/>；
    /// 缺省＝<see cref="SelectorActions.Unknown"/>）。由 <see cref="SelectorParameter.Action"/> 注入。
    /// </summary>
    public string Action { get; internal set; } = SelectorActions.Unknown;

    /// <summary>选择器类型名（专门选择器的稳定标识；前端据以选择视觉实现）。</summary>
    public string SelectorName { get; }

    /// <summary>交互模式（点选/拖拽）。</summary>
    public SelectorInteractionMode Mode { get; }

    /// <summary>候选引用集（引用类；非引用类＝空）。</summary>
    public IReadOnlyList<Ref<Entity>> Candidates { get; }

    /// <summary>候选标识集（非引用类：选项/卡牌名单；引用类＝null）。</summary>
    public IReadOnlyList<string>? Identifiers { get; }

    /// <summary>至少须选到的个数。</summary>
    public int Min { get; }

    /// <summary>至多可选的个数。</summary>
    public int Max { get; }

    /// <summary>选择器参数（呈现/引导用；纯交付数据）。</summary>
    public object? Parameter { get; }

    /// <summary>是否携带参数（与"未绑定"区分）。</summary>
    public bool HasParameter { get; }
}
