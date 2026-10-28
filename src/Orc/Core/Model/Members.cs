namespace Orc.Core.Model;

/// <summary>
/// 预制体来源层（R1e 第 7 条：内核基础预制体与游戏层 <c>extends</c> 追加的成员同处 <c>engine</c> 根下，
/// 解析对来源层无差别，但审查面要标出来源层）。
/// </summary>
public enum PrefabLayer
{
    /// <summary>内核层（<c>src/Orc</c> 提供的基础预制体）。</summary>
    Kernel,

    /// <summary>游戏层（<c>src/Orc.Game</c> 等 <c>extends</c> 内核基础预制体的层）。</summary>
    Game,

    /// <summary>资产层（卡包／samples 提供的预制体）。</summary>
    Asset,

    /// <summary>运行期注册（运行时向引擎注册的成员，R1e）。</summary>
    Runtime,
}

/// <summary>
/// 成员自有边在边集合里的位次（R1i 修订 2026-10-26，S13 第一轮证伪"自有边固定最前"）。
/// <para>
/// 旧动作管道的"转发"（`Commit`）与"落地"（`Apply`）都是**链上最后一步**：若自有边一律最前，
/// 外来边与包装在此类成员上永不执行。因此成员自己声明位次：
/// </para>
/// <list type="bullet">
///   <item><see cref="First"/>（默认）＝流程型（打出／部署／指向）：自有边先跑，外来扩展续后；</item>
///   <item><see cref="Last"/>＝管道型（伤害处理／动作落地）：外来改写先跑，自有边最后跑。</item>
/// </list>
/// </summary>
public enum OwnEdgePosition
{
    /// <summary>自有边先执行（流程型成员的默认位次）。</summary>
    First,

    /// <summary>自有边最后执行（管道型／落地型成员）。</summary>
    Last,
}

/// <summary>
/// 成员的一条**自有边**（R1i）：成员所属实体自己声明的可执行逻辑。
/// <para>
/// 位次由所属成员的 <see cref="MemberDefinition.OwnEdgePosition"/> 决定（默认最前）。
/// 新模型不存在独立于边集合的 "core" 概念——核心逻辑就是自有边。这样"包装整个成员"才有唯一着力点。
/// </para>
/// </summary>
public sealed class OwnedEdge
{
    private readonly KernelHandler _handler;

    /// <summary>以已注册的 handler 资产构造自有边。</summary>
    /// <exception cref="ArgumentNullException">asset 为 null。</exception>
    public OwnedEdge(HandlerAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Asset = asset;
        _handler = asset.Handler;
    }

    /// <summary>以**内联**可执行体构造自有边（测试／引擎内置流程的短逻辑；审查面标注为 inline）。</summary>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    public OwnedEdge(KernelHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <summary>引用的 handler 资产（内联＝null）。</summary>
    public HandlerAsset? Asset { get; }

    /// <summary>可执行体。</summary>
    public KernelHandler Handler => _handler;

    /// <summary>执行一次。</summary>
    internal Task InvokeAsync(ViewCarrier view, Context ctx, CancellationToken ct) => _handler(view, ctx, ct);
}

/// <summary>
/// 成员的合法性检验（R1：触发器嵌入逻辑固定为「上下文视图 + 合法性检验 + 按顺序调用挂载的 handler」）。
/// 返回非 null＝本次不合法：**仅本次取消**（不执行自有边与外来边，不传播到调用方）。
/// </summary>
public interface IMemberValidation
{
    /// <summary>校验；返回 null＝合法，非 null＝拒绝原因。</summary>
    string? Validate(ViewCarrier view, Context ctx);
}

/// <summary>合法性检验的委托实现（装配期声明）。</summary>
public sealed class DelegateMemberValidation : IMemberValidation
{
    private readonly Func<ViewCarrier, Context, string?> _validate;

    /// <summary>以委托构造。</summary>
    /// <exception cref="ArgumentNullException">validate 为 null。</exception>
    public DelegateMemberValidation(Func<ViewCarrier, Context, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        _validate = validate;
    }

    /// <inheritdoc />
    public string? Validate(ViewCarrier view, Context ctx) => _validate(view, ctx);
}

/// <summary>
/// 成员声明（预制体的一项成员）。
/// <para>
/// **不存在"主动／被动"分类字段**（R1 验收）：成员＝视图声明 + 可见性 + 自有边集合 + 可选合法性检验。
/// "触发器"与"handler"的差别只是"是否有嵌入逻辑"，而不是一个类型字段——任何成员都可被挂到别的成员上，
/// 并保留自己的视图与合法性检验。
/// </para>
/// </summary>
public sealed class MemberDefinition
{
    /// <summary>创建成员声明。</summary>
    /// <param name="name">成员名（允许含 <c>.</c>，如 <c>card.destroyed</c>；不得为空或纯空白）。</param>
    /// <param name="view">视图声明（默认空视图）。</param>
    /// <param name="visibility">可见性位标志（默认全关＝私有）。</param>
    /// <param name="ownEdges">自有边（声明序；固定最前执行）。</param>
    /// <param name="validation">合法性检验（可选）。</param>
    /// <param name="layer">来源层（默认内核）。</param>
    /// <param name="declaredBy">声明该成员的预制体 id（可选；审查/诊断用）。</param>
    /// <param name="signalTiming">
    /// 信号时机（null＝本成员**不是信号**）。
    /// <para>
    /// "是不是信号"不是一个新的分类字段，而是"这个成员可作为某个时机的被调用入口"这一事实的声明：
    /// 原 <c>hooks</c> 里的每个更新字符串，都变成一个 <c>engine#&lt;信号名&gt;</c> 成员（S3 第 1 条）。
    /// 可被挂载（＝订阅）由 <see cref="MemberVisibility.Hostable"/> 决定，与是否为信号无关。
    /// </para>
    /// </param>
    /// <param name="ownEdgePosition">自有边位次（默认 <see cref="OwnEdgePosition.First"/>；管道型成员用 Last）。</param>
    /// <exception cref="ArgumentException">name 为空或纯空白。</exception>
    public MemberDefinition(
        string name,
        ViewSpec? view = null,
        MemberVisibility visibility = MemberVisibility.None,
        IEnumerable<OwnedEdge>? ownEdges = null,
        IMemberValidation? validation = null,
        PrefabLayer layer = PrefabLayer.Kernel,
        string? declaredBy = null,
        SignalTiming? signalTiming = null,
        OwnEdgePosition ownEdgePosition = OwnEdgePosition.First)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("成员名不能为空或纯空白（成员名即寻址用的 <成员名> 段）。", nameof(name));
        }

        Name = name;
        View = view ?? ViewSpec.Empty;
        Visibility = visibility;
        OwnEdges = ownEdges?.ToArray() ?? Array.Empty<OwnedEdge>();
        Validation = validation;
        Layer = layer;
        DeclaredBy = declaredBy;
        SignalTiming = signalTiming;
        OwnEdgePosition = ownEdgePosition;
    }

    /// <summary>成员名（寻址 <c>作用域#成员名</c> 的成员名段；零转换保留，允许含 <c>.</c>）。</summary>
    public string Name { get; }

    /// <summary>视图声明（本成员读写的公共变量）。</summary>
    public ViewSpec View { get; }

    /// <summary>可见性（位标志；默认全关）。</summary>
    public MemberVisibility Visibility { get; }

    /// <summary>自有边（位次由 <see cref="OwnEdgePosition"/> 决定；默认最前）。</summary>
    public IReadOnlyList<OwnedEdge> OwnEdges { get; }

    /// <summary>自有边位次（默认 <see cref="OwnEdgePosition.First"/>；管道型成员声明 Last）。</summary>
    public OwnEdgePosition OwnEdgePosition { get; }

    /// <summary>合法性检验（null＝恒合法）。</summary>
    public IMemberValidation? Validation { get; }

    /// <summary>来源层（内核／游戏／资产／运行期）。</summary>
    public PrefabLayer Layer { get; }

    /// <summary>声明该成员的预制体 id（审查／诊断）。</summary>
    public string? DeclaredBy { get; }

    /// <summary>
    /// 信号时机（null＝**不是信号**）：非 null 表示本成员是一个"时机入口"，
    /// 可被 <c>EmitAsync</c> 调用、可被挂载声明订阅（S3）。
    /// </summary>
    public SignalTiming? SignalTiming { get; }

    /// <summary>是否是信号成员（时机入口）。</summary>
    public bool IsSignal => SignalTiming is not null;

    /// <summary>是否具备某能力位。</summary>
    public bool Has(MemberVisibility flag) => (Visibility & flag) == flag;

    /// <summary>覆写本成员（R3a：整段替换；可见性只能收紧）。</summary>
    /// <exception cref="InvalidOperationException">子声明的可见性放宽了父声明。</exception>
    internal MemberDefinition Override(MemberDefinition child)
    {
        if ((child.Visibility & ~Visibility) != 0)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Visibility,
                $"成员 '{Name}' 的覆写放大了可见性（父＝{Visibility}，子＝{child.Visibility}）：继承只能收紧、不能放宽（R5）。");
        }

        return child;
    }
}

/// <summary>
/// 私有字段声明（R3e：私有变量由实体预制体声明，随实体实例存在，只经 <c>owner.&lt;Name&gt;</c> 访问，**不进数据面**）。
/// </summary>
public sealed class PrivateFieldDefinition
{
    /// <summary>创建私有字段声明。</summary>
    /// <param name="name">字段名（合法标识符；供 <c>owner.&lt;Name&gt;</c> 按名访问）。</param>
    /// <param name="type">封闭内建类型。</param>
    /// <param name="initialValue">初值（须符合类型；null＝类型默认值）。</param>
    /// <exception cref="ArgumentException">字段名不是合法标识符。</exception>
    /// <exception cref="InvalidCastException">初值不符合声明类型。</exception>
    public PrivateFieldDefinition(string name, ViewFieldType type, object? initialValue = null)
    {
        if (!ViewFieldDefinition.IsIdentifier(name))
        {
            throw new ArgumentException(
                $"私有字段名 '{name}' 不是合法标识符（须可写作 owner.<Name>）。", nameof(name));
        }

        if (initialValue is not null && !ViewFieldTypes.Accepts(type, initialValue))
        {
            throw new InvalidCastException(
                $"私有字段 '{name}' 的初值类型 {initialValue.GetType().Name} 不符合声明类型 {ViewFieldTypes.Format(type)}。");
        }

        Name = name;
        Type = type;
        InitialValue = initialValue ?? ViewFieldTypes.DefaultValue(type);
    }

    /// <summary>字段名。</summary>
    public string Name { get; }

    /// <summary>封闭内建类型。</summary>
    public ViewFieldType Type { get; }

    /// <summary>初值（实例构造时复制；数组初值按引用复制——声明方应保证初值不可变）。</summary>
    public object? InitialValue { get; }
}
