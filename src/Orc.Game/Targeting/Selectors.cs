namespace Orc.Game.Targeting;

/// <summary>专门选择器名（稳定标识；前端据以选择视觉实现）。并入现役选择器雏形（Q10）。</summary>
public static class SelectorNames
{
    public const string FieldUnit = "fieldUnit";
    public const string HandOrigin = "handOrigin";
    public const string UnitHandDrag = "unitHandDrag";
    public const string AfterPlacement = "afterPlacement";
    public const string Slot = "slot";
    public const string TargetPoint = "targetPoint";
    public const string Hand = "hand";
    public const string Mulligan = "mulligan";
    public const string Option = "option";
    public const string CardPicker = "cardPicker";
    public const string CardPickerReference = "cardPickerReference";
}

/// <summary>
/// 选靶动作类别（R8 的 <c>pending.action</c> 取值域）：与 <see cref="SelectorNames"/> 同址集中，全代码库不写裸字符串。
/// 单一真源——投影/桥/日志三处都读这里，避免各自维护一套标签。
/// </summary>
public static class SelectorActions
{
    /// <summary>攻击选靶（指挥攻击）。</summary>
    public const string Attack = "attack";

    /// <summary>移动选靶（指挥移动；含后撤到己方支援线）。</summary>
    public const string Move = "move";

    /// <summary>部署空隙的选择（<c>play.unit</c> 的落位本身；部署过程中卡效果再发起的选牌/选单位属 <see cref="Effect"/>）。</summary>
    public const string Deploy = "deploy";

    /// <summary>卡效果发起的选靶（卡文本/词条/钳击等）。</summary>
    public const string Effect = "effect";

    /// <summary>换牌（起手调度）。</summary>
    public const string Mulligan = "mulligan";

    /// <summary>
    /// 指挥拖拽的合并请求（移动 ∪ 攻击候选并集）：发布时无法确定单一类别，故单列——
    /// 不用<b>并集里的任一类</b>冒充，否则类别就成了猜测。
    /// </summary>
    public const string Command = "command";

    /// <summary>未声明类别（构造方未给 <see cref="SelectorParameter.Action"/>）：不伪造类别。</summary>
    public const string Unknown = "unknown";
}

public sealed class HandOriginSelector : SingleReferenceSelector
{
    public HandOriginSelector() : base(SelectorNames.HandOrigin, SelectorInteractionMode.Click) { }
}

public sealed class UnitHandDragSelector : SingleReferenceSelector
{
    public UnitHandDragSelector() : base(SelectorNames.UnitHandDrag, SelectorInteractionMode.Drag) { }
}

public sealed class AfterPlacementSelector : SingleReferenceSelector
{
    public AfterPlacementSelector() : base(SelectorNames.AfterPlacement, SelectorInteractionMode.Click) { }
}

public sealed class FieldUnitSelector : SingleReferenceSelector
{
    public FieldUnitSelector() : base(SelectorNames.FieldUnit, SelectorInteractionMode.Drag) { }
}

public sealed class SlotSelector : SingleReferenceSelector
{
    public SlotSelector() : base(SelectorNames.Slot, SelectorInteractionMode.Click) { }
}

public sealed class TargetPointSelector : SingleReferenceSelector
{
    public TargetPointSelector() : base(SelectorNames.TargetPoint, SelectorInteractionMode.Click) { }
}

public sealed class HandSelector : MultiReferenceSelector
{
    public HandSelector() : base(SelectorNames.Hand, SelectorInteractionMode.Click) { }
}

public sealed class MulliganSelector : MultiReferenceSelector
{
    public MulliganSelector() : base(SelectorNames.Mulligan, SelectorInteractionMode.Click) { }
}

public sealed class OptionSelector : SingleIdentifierSelector
{
    public OptionSelector() : base(SelectorNames.Option, SelectorInteractionMode.Click) { }
}

public sealed class CardPickerListSelector : MultiIdentifierSelector
{
    public CardPickerListSelector() : base(SelectorNames.CardPicker, SelectorInteractionMode.Click) { }
}

public sealed class CardPickerSetSelector : MultiReferenceSelector
{
    public CardPickerSetSelector() : base(SelectorNames.CardPickerReference, SelectorInteractionMode.Click) { }
}

/// <summary>选择器模板（预构建定义实例；进程内共用）。</summary>
public static class SelectorTemplates
{
    public static FieldUnitSelector FieldUnit { get; } = new();
    public static HandOriginSelector HandOrigin { get; } = new();
    public static UnitHandDragSelector UnitHandDrag { get; } = new();
    public static AfterPlacementSelector AfterPlacement { get; } = new();
    public static SlotSelector Slot { get; } = new();
    public static TargetPointSelector TargetPoint { get; } = new();
    public static HandSelector Hand { get; } = new();
    public static MulliganSelector Mulligan { get; } = new();
    public static OptionSelector Option { get; } = new();
    public static CardPickerListSelector CardPickerList { get; } = new();
    public static CardPickerSetSelector CardPickerSet { get; } = new();
}