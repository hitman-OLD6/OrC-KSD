using Orc.Core;

namespace Orc.Game.Targeting;

// ---------------------------------------------------------------------------
// 参数（请求级；组装 targeter 时提供）
// ---------------------------------------------------------------------------

/// <summary>选择器参数基类。</summary>
public abstract class SelectorParameter
{
    /// <summary>
    /// 选靶归属玩家（多人支持 S6）：用于把待决请求路由到对应座位的控制器。
    /// <para><c>null</c>＝未指定（宿主侧回落"当前动作发起者"并记录诊断，不静默路由错）。</para>
    /// </summary>
    public Orc.Game.Players.Player? Owner { get; init; }
}

/// <summary>
/// 引用集参数（引用类选择器）：候选快照（后端构造方给出，Q19＝a 取消收集）＋数量约束＋域判定＋呈现参数。
/// 域判定与候选叠加：候选＝快照范围、域判定＝动态成员性（如"仍在手牌"）。
/// </summary>
public sealed class ReferenceSetParameter : SelectorParameter
{
    /// <summary>创建引用集参数。</summary>
    /// <param name="candidates">候选引用快照（不可含 null）。</param>
    /// <param name="min">至少须选到的个数（≥0）。</param>
    /// <param name="max">至多可选的个数（≥1）。</param>
    /// <param name="domainValidator">域判定面（可选；返回 false＝不合规；抛异常＝失败）。</param>
    /// <param name="tag">呈现参数（纯交付数据）。</param>
    public ReferenceSetParameter(
        IEnumerable<Ref<Entity>> candidates,
        int min,
        int max,
        Func<Ref<Entity>, bool>? domainValidator = null,
        object? tag = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var list = new List<Ref<Entity>>();
        foreach (var reference in candidates)
        {
            if (reference is null)
            {
                throw new ArgumentException("候选集含 null 元素。", nameof(candidates));
            }

            list.Add(reference);
        }

        if (min < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(min), min, "min 不能为负。");
        }

        if (max < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "max 须为正整数。");
        }

        if (min > max)
        {
            throw new ArgumentException($"min（{min}）不能大于 max（{max}）。", nameof(min));
        }

        Candidates = list.ToArray();
        Min = min;
        Max = max;
        DomainValidator = domainValidator;
        Tag = tag;
    }

    /// <summary>候选引用快照。</summary>
    public IReadOnlyList<Ref<Entity>> Candidates { get; }

    /// <summary>至少须选到的个数。</summary>
    public int Min { get; }

    /// <summary>至多可选的个数。</summary>
    public int Max { get; }

    /// <summary>域判定面（可选）。</summary>
    public Func<Ref<Entity>, bool>? DomainValidator { get; }

    /// <summary>呈现参数（纯交付数据）。</summary>
    public object? Tag { get; }
}

/// <summary>标识集参数（非引用类选择器：选项 / 卡牌名单）。</summary>
public sealed class IdentifierSetParameter : SelectorParameter
{
    /// <summary>创建标识集参数。</summary>
    /// <param name="identifiers">标识集合（非空、不可含 null/空白）。</param>
    /// <param name="min">至少须选到的个数（≥0）。</param>
    /// <param name="max">至多可选的个数（≥1）。</param>
    /// <param name="tag">呈现参数（纯交付数据）。</param>
    public IdentifierSetParameter(IEnumerable<string> identifiers, int min, int max, object? tag = null)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        var list = new List<string>();
        foreach (var identifier in identifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException("标识集含 null/空白元素。", nameof(identifiers));
            }

            list.Add(identifier);
        }

        if (list.Count == 0)
        {
            throw new ArgumentException("标识集为空（非引用类候选为空＝构造期错误）。", nameof(identifiers));
        }

        if (min < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(min), min, "min 不能为负。");
        }

        if (max < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "max 须为正整数。");
        }

        if (min > max)
        {
            throw new ArgumentException($"min（{min}）不能大于 max（{max}）。", nameof(min));
        }

        Identifiers = list.ToArray();
        Min = min;
        Max = max;
        Tag = tag;
    }

    /// <summary>候选标识（声明/载荷集合）。</summary>
    public IReadOnlyList<string> Identifiers { get; }

    /// <summary>至少须选到的个数。</summary>
    public int Min { get; }

    /// <summary>至多可选的个数。</summary>
    public int Max { get; }

    /// <summary>呈现参数（纯交付数据）。</summary>
    public object? Tag { get; }
}

// ---------------------------------------------------------------------------
// 定义（模板；进程内共用、无运行期身份）
// ---------------------------------------------------------------------------

/// <summary>选择器（非泛型基类；供异构选择器列表持有）。</summary>
public abstract class Selector
{
    /// <summary>创建选择器定义。</summary>
    /// <param name="name">类型名（专门选择器的稳定标识）。</param>
    /// <param name="mode">交互模式。</param>
    protected Selector(string name, SelectorInteractionMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Mode = mode;
    }

    /// <summary>类型名（专门选择器的稳定标识；前端据以选择视觉实现）。</summary>
    public string Name { get; }

    /// <summary>交互模式（点选/拖拽；决定空提交语义）。</summary>
    public SelectorInteractionMode Mode { get; }
}

/// <summary>
/// 选择器<b>定义</b>（模板）：类型名（前端据以选视觉）＋交互模式（点选/拖拽）；
/// 在组装 targeter 时以参数创建<b>实例</b>（Q13a：定义/实例分离）。
/// </summary>
public abstract class Selector<TResult> : Selector
{
    /// <summary>创建选择器定义。</summary>
    protected Selector(string name, SelectorInteractionMode mode) : base(name, mode)
    {
    }

    /// <summary>创建运行实例（框架内部）。</summary>
    internal abstract SelectorInstance<TResult> CreateInstance(SelectorParameter parameter);
}

// ---------------------------------------------------------------------------
// 实例（运行期；携带身份、绑定参数、承载结果）
// ---------------------------------------------------------------------------

/// <summary>选择器实例（非泛型基面）：前端经此取呈现数据、提交语义事件。</summary>
public interface ISelectorInstance
{
    /// <summary>实例标识（稳定；重试重入时为同一实例、同一标识）。</summary>
    string Id { get; }

    /// <summary>选择器类型名（同定义）。</summary>
    string SelectorName { get; }

    /// <summary>交互模式。</summary>
    SelectorInteractionMode Mode { get; }

    /// <summary>呈现数据（交付前端）。</summary>
    SelectorPresentation Presentation { get; }

    /// <summary>是否已构成终局。</summary>
    bool IsCompleted { get; }

    /// <summary>
    /// 提交语义事件（前端归一后的手势）；返回是否被接受（构成终局）。
    /// 判定与校验归后端实例（I2a＝c / I4＝a）。
    /// </summary>
    bool Submit(SelectorEvent selectorEvent);
}

/// <summary>选择器实例（泛型实现；承载强类型结果）。</summary>
public sealed class SelectorInstance<TResult> : ISelectorInstance
{
    private readonly object _sync = new();
    private readonly Func<SelectorEvent, SelectorResult<TResult>> _judge;
    private TaskCompletionSource<SelectorResult<TResult>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    internal SelectorInstance(
        string id,
        Selector<TResult> definition,
        IReadOnlyList<Ref<Entity>> candidates,
        IReadOnlyList<string>? identifiers,
        int min,
        int max,
        object? tag,
        Func<SelectorEvent, SelectorResult<TResult>> judge)
    {
        Id = id;
        SelectorName = definition.Name;
        Mode = definition.Mode;
        _judge = judge;
        Presentation = new SelectorPresentation(
            definition.Name,
            definition.Mode,
            candidates,
            identifiers,
            min,
            max,
            tag,
            hasParameter: tag is not null);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string SelectorName { get; }

    /// <inheritdoc />
    public SelectorInteractionMode Mode { get; }

    /// <inheritdoc />
    public SelectorPresentation Presentation { get; }

    /// <inheritdoc />
    public bool IsCompleted
    {
        get
        {
            lock (_sync)
            {
                return _completed;
            }
        }
    }

    /// <summary>终局任务（targeter 内部 await；强类型）。</summary>
    internal Task<SelectorResult<TResult>> Completion
    {
        get
        {
            lock (_sync)
            {
                return _completion.Task;
            }
        }
    }

    /// <inheritdoc />
    public bool Submit(SelectorEvent selectorEvent)
    {
        ArgumentNullException.ThrowIfNull(selectorEvent);

        lock (_sync)
        {
            if (_completed)
            {
                return false;
            }

            SelectorResult<TResult> result;
            try
            {
                result = _judge(selectorEvent);
            }
            catch
            {
                result = SelectorResult<TResult>.Failed(SelectorFailureReason.Fault);
            }

            _completed = true;
            _completion.TrySetResult(result);
            return true;
        }
    }

    /// <summary>重入（重试）：重置为"未完成"——同一实例、同一标识（框架内部）。</summary>
    internal bool Reopen()
    {
        lock (_sync)
        {
            if (!_completed)
            {
                return false;
            }

            _completed = false;
            _completion = new TaskCompletionSource<SelectorResult<TResult>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }
}

// ---------------------------------------------------------------------------
// 基类选择器（组合"类型名 + 交互模式"，判定逻辑复用）
// ---------------------------------------------------------------------------

/// <summary>引用单选选择器（产 <see cref="Ref{T}"/>；min＝max＝1）。</summary>
public class SingleReferenceSelector : Selector<Ref<Entity>>
{
    /// <summary>创建引用单选选择器。</summary>
    public SingleReferenceSelector(string name, SelectorInteractionMode mode)
        : base(name, mode)
    {
    }

    internal override SelectorInstance<Ref<Entity>> CreateInstance(SelectorParameter parameter)
    {
        var p = SelectorJudge.RequireReferenceSet(parameter);
        return new SelectorInstance<Ref<Entity>>(
            Guid.NewGuid().ToString("N"),
            this,
            p.Candidates,
            null,
            p.Min,
            p.Max,
            p.Tag,
            e => SelectorJudge.JudgeSingleReference(this, p, e));
    }
}

/// <summary>引用多选选择器（产 <see cref="IReadOnlyList{T}"/>）。</summary>
public class MultiReferenceSelector : Selector<IReadOnlyList<Ref<Entity>>>
{
    /// <summary>创建引用多选选择器。</summary>
    public MultiReferenceSelector(string name, SelectorInteractionMode mode)
        : base(name, mode)
    {
    }

    internal override SelectorInstance<IReadOnlyList<Ref<Entity>>> CreateInstance(SelectorParameter parameter)
    {
        var p = SelectorJudge.RequireReferenceSet(parameter);
        return new SelectorInstance<IReadOnlyList<Ref<Entity>>>(
            Guid.NewGuid().ToString("N"),
            this,
            p.Candidates,
            null,
            p.Min,
            p.Max,
            p.Tag,
            e => SelectorJudge.JudgeMultiReference(this, p, e));
    }
}

/// <summary>标识单选选择器（产 <see cref="string"/>；如选项）。</summary>
public class SingleIdentifierSelector : Selector<string>
{
    /// <summary>创建标识单选选择器。</summary>
    public SingleIdentifierSelector(string name, SelectorInteractionMode mode)
        : base(name, mode)
    {
    }

    internal override SelectorInstance<string> CreateInstance(SelectorParameter parameter)
    {
        var p = SelectorJudge.RequireIdentifierSet(parameter);
        return new SelectorInstance<string>(
            Guid.NewGuid().ToString("N"),
            this,
            Array.Empty<Ref<Entity>>(),
            p.Identifiers,
            p.Min,
            p.Max,
            p.Tag,
            e => SelectorJudge.JudgeSingleIdentifier(this, p, e));
    }
}

/// <summary>标识多选选择器（产 <see cref="IReadOnlyList{T}"/>；如卡牌名单）。</summary>
public class MultiIdentifierSelector : Selector<IReadOnlyList<string>>
{
    /// <summary>创建标识多选选择器。</summary>
    public MultiIdentifierSelector(string name, SelectorInteractionMode mode)
        : base(name, mode)
    {
    }

    internal override SelectorInstance<IReadOnlyList<string>> CreateInstance(SelectorParameter parameter)
    {
        var p = SelectorJudge.RequireIdentifierSet(parameter);
        return new SelectorInstance<IReadOnlyList<string>>(
            Guid.NewGuid().ToString("N"),
            this,
            Array.Empty<Ref<Entity>>(),
            p.Identifiers,
            p.Min,
            p.Max,
            p.Tag,
            e => SelectorJudge.JudgeMultiIdentifier(this, p, e));
    }
}

// ---------------------------------------------------------------------------
// 判定与校验（后端实例内；I4＝a）
// ---------------------------------------------------------------------------

internal static class SelectorJudge
{
    internal static ReferenceSetParameter RequireReferenceSet(SelectorParameter parameter)
        => parameter as ReferenceSetParameter
           ?? throw new ArgumentException("选择器需要引用集参数（ReferenceSetParameter）。", nameof(parameter));

    internal static IdentifierSetParameter RequireIdentifierSet(SelectorParameter parameter)
        => parameter as IdentifierSetParameter
           ?? throw new ArgumentException("选择器需要标识集参数（IdentifierSetParameter）。", nameof(parameter));

    // ---------- 引用单选 ----------

    internal static SelectorResult<Ref<Entity>> JudgeSingleReference(
        SingleReferenceSelector definition,
        ReferenceSetParameter p,
        SelectorEvent selectorEvent)
    {
        switch (selectorEvent)
        {
            case CancelEvent:
                return SelectorResult<Ref<Entity>>.Cancelled();

            case DropEvent drop:
                if (drop.Target is null)
                {
                    return SelectorResult<Ref<Entity>>.Cancelled();
                }

                return ValidateSingle(p, drop.Target!, out var dropped)
                    ? SelectorResult<Ref<Entity>>.Ok(dropped)
                    : FailedSingle();

            case PickEvent pick:
                if (pick.References.Count == 0)
                {
                    // 点选空提交＝非法选择（Q20）；拖拽空提交＝取消。
                    return definition.Mode == SelectorInteractionMode.Drag
                        ? SelectorResult<Ref<Entity>>.Cancelled()
                        : FailedSingle();
                }

                if (pick.References.Count != 1)
                {
                    return FailedSingle();
                }

                return ValidateSingle(p, pick.References[0], out var picked)
                    ? SelectorResult<Ref<Entity>>.Ok(picked)
                    : FailedSingle();

            default:
                return FailedSingle();
        }
    }

    private static SelectorResult<Ref<Entity>> FailedSingle()
        => SelectorResult<Ref<Entity>>.Failed(SelectorFailureReason.InvalidSelection);

    private static bool ValidateSingle(ReferenceSetParameter p, Ref<Entity> reference, out Ref<Entity> validated)
    {
        validated = reference;
        if (!p.Candidates.Contains(reference))
        {
            return false;
        }

        if (!reference.IsAlive)
        {
            return false;
        }

        if (p.DomainValidator is not null && !p.DomainValidator(reference))
        {
            return false;
        }

        return true;
    }

    // ---------- 引用多选 ----------

    internal static SelectorResult<IReadOnlyList<Ref<Entity>>> JudgeMultiReference(
        MultiReferenceSelector definition,
        ReferenceSetParameter p,
        SelectorEvent selectorEvent)
    {
        switch (selectorEvent)
        {
            case CancelEvent:
                return SelectorResult<IReadOnlyList<Ref<Entity>>>.Cancelled();

            case DropEvent drop:
                if (drop.Target is null)
                {
                    return SelectorResult<IReadOnlyList<Ref<Entity>>>.Cancelled();
                }

                return ValidateMany(p, new[] { drop.Target! }, out var dropped)
                    ? SelectorResult<IReadOnlyList<Ref<Entity>>>.Ok(dropped)
                    : FailedMany();

            case PickEvent pick:
                if (pick.References.Count == 0)
                {
                    if (definition.Mode == SelectorInteractionMode.Drag)
                    {
                        return SelectorResult<IReadOnlyList<Ref<Entity>>>.Cancelled();
                    }

                    // 点选空提交：min=0＝空选合法（Ok 空，如"不换牌"）；min>0＝非法。
                    return p.Min == 0
                        ? SelectorResult<IReadOnlyList<Ref<Entity>>>.Ok(Array.Empty<Ref<Entity>>())
                        : FailedMany();
                }

                return ValidateMany(p, pick.References, out var picked)
                    ? SelectorResult<IReadOnlyList<Ref<Entity>>>.Ok(picked)
                    : FailedMany();

            default:
                return FailedMany();
        }
    }

    private static SelectorResult<IReadOnlyList<Ref<Entity>>> FailedMany()
        => SelectorResult<IReadOnlyList<Ref<Entity>>>.Failed(SelectorFailureReason.InvalidSelection);

    private static bool ValidateMany(
        ReferenceSetParameter p,
        IReadOnlyList<Ref<Entity>> submitted,
        out IReadOnlyList<Ref<Entity>> validated)
    {
        validated = Array.Empty<Ref<Entity>>();
        if (submitted.Count < p.Min || submitted.Count > p.Max)
        {
            return false;
        }

        var list = new List<Ref<Entity>>(submitted.Count);
        foreach (var reference in submitted)
        {
            if (reference is null || !p.Candidates.Contains(reference) || !reference.IsAlive)
            {
                return false;
            }

            if (p.DomainValidator is not null && !p.DomainValidator(reference))
            {
                return false;
            }

            list.Add(reference);
        }

        validated = list.ToArray();
        return true;
    }

    // ---------- 标识单选 ----------

    internal static SelectorResult<string> JudgeSingleIdentifier(
        SingleIdentifierSelector definition,
        IdentifierSetParameter p,
        SelectorEvent selectorEvent)
    {
        switch (selectorEvent)
        {
            case CancelEvent:
                return SelectorResult<string>.Cancelled();

            case PickEvent pick:
                if (pick.Identifiers.Count == 0)
                {
                    return definition.Mode == SelectorInteractionMode.Drag
                        ? SelectorResult<string>.Cancelled()
                        : FailedIdentifier();
                }

                if (pick.Identifiers.Count != 1 || !p.Identifiers.Contains(pick.Identifiers[0]))
                {
                    return FailedIdentifier();
                }

                return SelectorResult<string>.Ok(pick.Identifiers[0]);

            default:
                return FailedIdentifier();
        }
    }

    private static SelectorResult<string> FailedIdentifier()
        => SelectorResult<string>.Failed(SelectorFailureReason.InvalidSelection);

    // ---------- 标识多选 ----------

    internal static SelectorResult<IReadOnlyList<string>> JudgeMultiIdentifier(
        MultiIdentifierSelector definition,
        IdentifierSetParameter p,
        SelectorEvent selectorEvent)
    {
        switch (selectorEvent)
        {
            case CancelEvent:
                return SelectorResult<IReadOnlyList<string>>.Cancelled();

            case PickEvent pick:
                if (pick.Identifiers.Count == 0)
                {
                    if (definition.Mode == SelectorInteractionMode.Drag)
                    {
                        return SelectorResult<IReadOnlyList<string>>.Cancelled();
                    }

                    return p.Min == 0
                        ? SelectorResult<IReadOnlyList<string>>.Ok(Array.Empty<string>())
                        : SelectorResult<IReadOnlyList<string>>.Failed(SelectorFailureReason.InvalidSelection);
                }

                if (pick.Identifiers.Count < p.Min || pick.Identifiers.Count > p.Max)
                {
                    return SelectorResult<IReadOnlyList<string>>.Failed(SelectorFailureReason.InvalidSelection);
                }

                foreach (var identifier in pick.Identifiers)
                {
                    if (!p.Identifiers.Contains(identifier))
                    {
                        return SelectorResult<IReadOnlyList<string>>.Failed(SelectorFailureReason.InvalidSelection);
                    }
                }

                return SelectorResult<IReadOnlyList<string>>.Ok(pick.Identifiers.ToArray());

            default:
                return SelectorResult<IReadOnlyList<string>>.Failed(SelectorFailureReason.InvalidSelection);
        }
    }
}
