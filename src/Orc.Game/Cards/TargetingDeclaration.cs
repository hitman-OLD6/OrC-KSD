using Orc.Cards;
using Orc.Core;
using Orc.Game.Judicators;
using Orc.Game.Players;

namespace Orc.Game.Cards;

/// <summary>
/// 目标可选性声明（S3）：单位声明「某范围内的目标不可被某类来源指向」——
/// 以**包装目标候选名单判定器**（After：从名单中剔除被保护目标）实现；来源离场/注销时撤销。
/// <para>生命周期＝效果包裹：创建者（词条组件 / 效果动作）负责调用 <see cref="Attach"/> 与 <see cref="Detach"/>；
/// 本类型只持有句柄与判定参数，不自行调度。</para>
/// <para>方向：仅对「与来源归属对立」的来源生效（敌方指令）；作用域由 <c>protects</c> 提供（self/friendly/...）。</para>
/// <para>离场兜底：包装器求值时校验 <c>isActive</c>（来源在场），避免"离场但不卸载词条"路径残留。</para>
/// </summary>
public sealed class TargetingDeclaration
{
    private readonly JudicatorRegistry _registry;
    private readonly Card _source;
    private readonly Func<Card, bool> _protects;
    private readonly Func<bool> _isActive;
    private readonly string _sourceKind;

    private JudicatorWrapperRegistration? _wrapper;

    /// <summary>创建声明（创建者负责 Attach/Detach 生命周期）。</summary>
    public TargetingDeclaration(
        JudicatorRegistry registry,
        Card source,
        Func<Card, bool> protects,
        Func<bool> isActive,
        string sourceKind)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(protects);
        ArgumentNullException.ThrowIfNull(isActive);
        _registry = registry;
        _source = source;
        _protects = protects;
        _isActive = isActive;
        _sourceKind = sourceKind;
    }

    /// <summary>挂载到指定玩家域（对手域＝其指令的候选名单所在域）。</summary>
    public void Attach(int playerIndex)
    {
        if (_wrapper is not null)
        {
            return;
        }

        var registration = _registry.Resolve(JudicatorNames.TargetCandidateEligibility);
        _wrapper = _registry.RegisterAfterWrapper(registration, Filter, playerIndex);
    }

    /// <summary>撤销（幂等）。</summary>
    public void Detach()
    {
        if (_wrapper is null)
        {
            return;
        }

        _registry.UnregisterWrapper(_wrapper);
        _wrapper = null;
    }

    private object[]? Filter(object[]? args, object[]? result)
    {
        if (result is not { Length: 1 } || result[0] is not IReadOnlyList<Ref<Entity>> list)
        {
            return result;
        }

        if (args is not { Length: > 0 } || args[0] is not Card viewer)
        {
            return result;
        }

        if (!_isActive())
        {
            return result; // 来源已离场：不生效（兜底）
        }

        if (!IsOpposing(viewer))
        {
            return result; // 仅敌方来源
        }

        if (!MatchesSourceKind(viewer))
        {
            return result;
        }

        var filtered = new List<Ref<Entity>>(list.Count);
        foreach (var reference in list)
        {
            if (reference.IsAlive && reference.Value is Card candidate && _protects(candidate))
            {
                continue; // 剔除被保护目标
            }

            filtered.Add(reference);
        }

        return new object[] { filtered };
    }

    private bool IsOpposing(Card viewer)
    {
        var sourceOwner = OwnerOf(_source);
        var viewerOwner = OwnerOf(viewer);
        return sourceOwner is not null && viewerOwner is not null && !ReferenceEquals(sourceOwner, viewerOwner);
    }

    private bool MatchesSourceKind(Card viewer) => _sourceKind switch
    {
        "command" => viewer is CommandCard,
        "unit-effect" => viewer is UnitCard,
        _ => true,
    };

    private static Player? OwnerOf(Card card) => card switch
    {
        Hq hq => hq.Owner,
        CardBase cardBase => cardBase.Owner,
        _ => null,
    };
}
