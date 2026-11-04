namespace Orc.Core.Model;

/// <summary>
/// **模板守卫档**（R32／D24）：模板成员在作者效果体**之前**插入的过滤代码种类。
/// <para>
/// 信号是全局广播（<c>engine#unit.deployed</c> 上任何单位部署都会跑到每条边），模板声明守卫后由
/// **生成器**合成过滤代码——作者不可见、不可编辑、不可删除。档的可用性只由**白名单**决定
/// （<c>Subjects</c>／<c>Players</c>），不按载荷顺序或字段类型推断（D27）。
/// </para>
/// <para>
/// 摊平产物**不含本声明**：守卫已烘进 csx 文本（D24／D25 推论）。产物若保留 <c>guard</c>，
/// 再次摊平会重复加守卫。
/// </para>
/// </summary>
public enum HandlerGuard
{
    /// <summary>不生成过滤代码（任何广播都执行；模板文案不显示本档）。</summary>
    Any = 0,

    /// <summary>本单位：宿主卡与载荷里的「主体卡字段」引用相等。</summary>
    Self = 1,

    /// <summary>友方：宿主卡的归属玩家与载荷里的玩家字段引用相等。</summary>
    OwnerSame = 2,

    /// <summary>敌方：宿主卡的归属玩家与载荷里的玩家字段不相等（<c>Owner</c> 为 null ＝按「不同主」）。</summary>
    OwnerDifferent = 3,
}

/// <summary>
/// 守卫的**代码合成参数**（生成器输入；纯数据）：档 ＋ 两个白名单解析出的载荷字段名。
/// <para>
/// 字段名来自信号定义侧的 <c>Subjects</c>／<c>Players</c> 白名单，**不由载荷顺序推断**——
/// 载荷里 <c>Player</c>／<c>Deck</c>／<c>Position</c>／<c>Source</c> 全是 <c>entity</c>，
/// 按「第一个实体字段」推断会把回合五连判成可用而比较对象恒不命中（D27）。
/// </para>
/// </summary>
/// <param name="Guard">守卫档（<see cref="HandlerGuard.Any"/>＝不合成，调用方不应构造本记录）。</param>
/// <param name="SubjectField">主体卡字段名（<c>self</c> 档用；如 <c>Unit</c>／<c>Card</c>）。</param>
/// <param name="PlayerField">玩家字段名（<c>owner-*</c> 档用；如 <c>Player</c>）。</param>
public sealed record HandlerGuardSpec(HandlerGuard Guard, string SubjectField, string PlayerField);

/// <summary>守卫的**文本名与中文名**（schema 取值 ＋ 模板文案派生；代码常量，不落数据文件）。</summary>
public static class HandlerGuardNames
{
    /// <summary><c>any</c> 档文本名。</summary>
    public const string Any = "any";

    /// <summary><c>self</c> 档文本名。</summary>
    public const string Self = "self";

    /// <summary><c>owner-same</c> 档文本名。</summary>
    public const string OwnerSame = "owner-same";

    /// <summary><c>owner-different</c> 档文本名。</summary>
    public const string OwnerDifferent = "owner-different";

    /// <summary>全部档的文本名（声明序：any／self／owner-same／owner-different）。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Any, Self, OwnerSame, OwnerDifferent };

    /// <summary>档 → 文本名。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义的档。</exception>
    public static string Format(HandlerGuard guard) => guard switch
    {
        HandlerGuard.Any => Any,
        HandlerGuard.Self => Self,
        HandlerGuard.OwnerSame => OwnerSame,
        HandlerGuard.OwnerDifferent => OwnerDifferent,
        _ => throw new ArgumentOutOfRangeException(nameof(guard), guard, "未定义的守卫档。"),
    };

    /// <summary>文本名 → 档（未知＝null）。</summary>
    public static HandlerGuard? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text.Trim() switch
        {
            Any => HandlerGuard.Any,
            Self => HandlerGuard.Self,
            OwnerSame => HandlerGuard.OwnerSame,
            OwnerDifferent => HandlerGuard.OwnerDifferent,
            _ => null,
        };
    }

    /// <summary>
    /// 档的中文名（R35 文案派生用；<see cref="HandlerGuard.Any"/>＝**null＝不显示**——`any` 档模板文案不带括号）。
    /// </summary>
    public static string? DisplayName(HandlerGuard guard) => guard switch
    {
        HandlerGuard.Self => "本单位",
        HandlerGuard.OwnerSame => "友方",
        HandlerGuard.OwnerDifferent => "敌方",
        _ => null,
    };
}

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
/// 成员的一条**自有边**（R1i）：成员所属实体自己声明的可执行逻辑。
/// <para>
/// 位次由所属成员的 <see cref="MemberDefinition.Band"/> 在 <see cref="MemberDefinition.Bands"/> 里的下标决定
/// （band 为空＝默认段＝下标 0）。新模型不存在独立于边集合的 "core" 概念——核心逻辑就是自有边。
/// 这样"包装整个成员"才有唯一着力点。
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
    /// <param name="band">
    /// 该成员**自有边**所属的段名（null＝默认段）。必须出现在 <paramref name="bands"/> 声明的方案里。
    /// </param>
    /// <param name="bands">
    /// 该成员的**段序声明**（有序段名，数值/下标小者先执行；null＝单段方案——所有边同段、按声明序）。
    /// </param>
    /// <param name="seams">
    /// 该成员 handler 内部的**具名插入点**声明（null／空＝不声明任何缝）。
    /// <para>
    /// 包装器可以挂到 <c>(成员, 缝名)</c> 上（<see cref="WrapDeclaration.Seam"/>）；handler 在内部某处调
    /// <c>ctx.SeamAsync(缝名)</c>，运行时按该缝的包装器列表**管道式**（LIFO）执行。
    /// 缝未在此声明＝调用 <c>ctx.SeamAsync</c> 抛 <see cref="KeyNotFoundException"/>；
    /// 声明了但没人挂＝空缝（调用不报错、无副作用）。
    /// </para>
    /// <para>
    /// 三层分工：<c>wrap.phase</c> 管成员外围、<see cref="Band"/> 管成员内部边次序、本属性管
    /// **handler 内部的具名插入点**（R1i 修订 2026-10-29：seam 恢复，不是退役）。
    /// </para>
    /// </param>
    /// <param name="sealedMember">
    /// **成员级封死**（R2／R39 裁决 5）：它锁定**这个 slot**（沿链向下生效）——把「已填可再填」降级为
    /// 「不可再填」。对非 slot 成员无意义（不再有资产级总闸）。
    /// <para><c>framework.</c> 前缀的框架合成成员**恒为 sealed**（沿用
    /// <see cref="ComponentDisposalPolicy.IsReservedMember"/> 的保留名判定，不新增名单）。</para>
    /// </param>
    /// <param name="slot">
    /// **成员级空位声明**（R39 裁决 5／8；null＝普通成员或纯编排成员）：链上声明过 ⇒ 可被填／重填。
    /// 与 <paramref name="hasHandler"/> 是**正交的两个事实**（D40）：本字段＝「这里有一个空位」，
    /// hasHandler＝「现在填了什么」；四行判定表就是两者的存在性组合，没有第三个状态位。
    /// </param>
    /// <param name="hasHandler">
    /// 该成员当前是否已有实现（资产层 `handler` 字段的存在性投影）。
    /// <para>
    /// 为什么要一个显式位：摊平器用 `ToStructuralPrefabDefinition`（不解析 handler）建目录，
    /// 那种投影里 `OwnEdges` 恒为空，无法从边数推出「有没有 handler」。
    /// </para>
    /// </param>
    /// <param name="guard">
    /// **守卫档**（R32／D24；null＝不守卫）：模板成员声明它，生成器在作者效果体之前合成过滤代码。
    /// 沿继承链**继承**（子覆写不重声明＝沿用父的档），因为作者看不见也改不到它。
    /// </param>
    /// <exception cref="ArgumentException">
    /// name 为空或纯空白；band 未在 bands 方案中声明；bands 含空白项或重复名；seams 含空白项或重复名。
    /// </exception>
    public MemberDefinition(
        string name,
        ViewSpec? view = null,
        MemberVisibility visibility = MemberVisibility.None,
        IEnumerable<OwnedEdge>? ownEdges = null,
        IMemberValidation? validation = null,
        PrefabLayer layer = PrefabLayer.Kernel,
        string? declaredBy = null,
        SignalTiming? signalTiming = null,
        string? band = null,
        IReadOnlyList<string>? bands = null,
        IReadOnlyList<string>? seams = null,
        bool sealedMember = false,
        HandlerGuard? guard = null,
        MemberSlotDeclaration? slot = null,
        bool hasHandler = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("成员名不能为空或纯空白（成员名即寻址用的 <成员名> 段）。", nameof(name));
        }

        var scheme = ValidateBands(name, bands);
        ValidateBand(name, band, scheme);

        Name = name;
        View = view ?? ViewSpec.Empty;
        Visibility = visibility;
        OwnEdges = ownEdges?.ToArray() ?? Array.Empty<OwnedEdge>();
        Validation = validation;
        Layer = layer;
        DeclaredBy = declaredBy;
        SignalTiming = signalTiming;
        Band = band;
        Bands = scheme;
        Seams = ValidateSeams(name, seams);
        Sealed = sealedMember;
        Guard = guard;
        Slot = slot;
        HasHandler = hasHandler;
    }

    /// <summary>以父守卫补全子成员（守卫沿继承链继承，R32：作者看不见也改不到）。</summary>
    internal MemberDefinition WithInheritedGuard(HandlerGuard? parentGuard)
        => parentGuard is null || Guard is not null
            ? this
            : new MemberDefinition(
                Name,
                View,
                Visibility,
                OwnEdges,
                Validation,
                Layer,
                DeclaredBy,
                SignalTiming,
                Band,
                Bands,
                Seams,
                Sealed,
                parentGuard,
                Slot,
                HasHandler);

    /// <summary>
    /// 以父 slot 补全子成员（R39 裁决 5／D39：填一次**不消灭**空位，只改变状态）。
    /// <para>
    /// 子没重声明 slot（草案只给了 handler）时沿用父的声明 ⇒ 产物保留 <c>slot ∧ handler</c>，
    /// 下一层草案仍可再填；子重声明时类型只能是 handler（构造期已保证），不存在类型分叉。
    /// </para>
    /// </summary>
    internal MemberDefinition WithInheritedSlot(MemberSlotDeclaration? parentSlot)
        => parentSlot is null || Slot is not null
            ? this
            : new MemberDefinition(
                Name,
                View,
                Visibility,
                OwnEdges,
                Validation,
                Layer,
                DeclaredBy,
                SignalTiming,
                Band,
                Bands,
                Seams,
                Sealed,
                Guard,
                parentSlot,
                HasHandler);

    /// <summary>
    /// 具名缝声明的合法性校验（fail-fast）：空白项、重复名一律拒绝（重复名会让"按名取包装器列表"变成多解）。
    /// </summary>
    /// <exception cref="ArgumentException">含空白项或重复名。</exception>
    private static IReadOnlyList<string> ValidateSeams(string memberName, IReadOnlyList<string>? seams)
    {
        if (seams is null || seams.Count == 0)
        {
            return Array.Empty<string>(); // 空／null ＝ 该成员不声明任何缝（与"声明了空缝"同义，见 Seams 注释）
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seam in seams)
        {
            if (string.IsNullOrWhiteSpace(seam))
            {
                throw new ArgumentException(
                    $"成员 '{memberName}' 的 seams 含空白缝名（缝名不能为空或纯空白）。", nameof(seams));
            }

            if (!seen.Add(seam))
            {
                throw new ArgumentException(
                    $"成员 '{memberName}' 的 seams 含重复缝名 '{seam}'（缝名必须唯一）。", nameof(seams));
            }
        }

        return seams.ToArray();
    }

    /// <summary>
    /// **段序方案**的合法性校验（fail-fast）：空白项、重复名一律拒绝（重复名会让"段下标"变成多解）。
    /// </summary>
    /// <exception cref="ArgumentException">含空白项或重复名。</exception>
    private static IReadOnlyList<string>? ValidateBands(string memberName, IReadOnlyList<string>? bands)
    {
        if (bands is null)
        {
            return null;
        }

        if (bands.Count == 0)
        {
            throw new ArgumentException(
                $"成员 '{memberName}' 的 bands 不能为空数组（null＝单段方案：所有边同段、按声明序）。",
                nameof(bands));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in bands)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException(
                    $"成员 '{memberName}' 的 bands 含空白段名（段名不能为空或纯空白）。", nameof(bands));
            }

            if (!seen.Add(segment))
            {
                throw new ArgumentException(
                    $"成员 '{memberName}' 的 bands 含重复段名 '{segment}'（段序方案里的段名必须唯一）。", nameof(bands));
            }
        }

        return bands.ToArray();
    }

    /// <summary>自有边段名的合法性校验（fail-fast）：段名必须已在方案中声明。</summary>
    /// <exception cref="ArgumentException">band 未在方案中声明，或声明了 band 却没有方案。</exception>
    private static void ValidateBand(string memberName, string? band, IReadOnlyList<string>? bands)
    {
        if (band is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(band))
        {
            throw new ArgumentException(
                $"成员 '{memberName}' 的 band 不能为空或纯空白（null＝默认段）。", nameof(band));
        }

        if (bands is null)
        {
            throw new ArgumentException(
                $"成员 '{memberName}' 声明了 band='{band}' 但没有声明 bands 方案：band 名未在成员的 bands 方案中声明"
                + "（先声明段序方案，或去掉 band 使用默认段）。",
                nameof(band));
        }

        if (!Contains(bands, band))
        {
            throw new ArgumentException(
                $"成员 '{memberName}' 的 band='{band}' 未在成员的 bands 方案中声明"
                + $"（已声明：{string.Join("／", bands)}）。",
                nameof(band));
        }
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>成员名（寻址 <c>作用域#成员名</c> 的成员名段；零转换保留，允许含 <c>.</c>）。</summary>
    public string Name { get; }

    /// <summary>视图声明（本成员读写的公共变量）。</summary>
    public ViewSpec View { get; }

    /// <summary>可见性（位标志；默认全关）。</summary>
    public MemberVisibility Visibility { get; }

    /// <summary>自有边（位次＝<see cref="Band"/> 在 <see cref="Bands"/> 里的下标；同段内按声明序）。</summary>
    public IReadOnlyList<OwnedEdge> OwnEdges { get; }

    /// <summary>
    /// 自有边所属的**段名**（null＝默认段；段下标 0）。
    /// <para>
    /// **自有边位次＝<see cref="Band"/> 在 <see cref="Bands"/> 中的下标**——"自有边最前／最后"不再是独立概念，
    /// 而是"自有边落在首段／末段"的结果。段名合法时也可能落在中间段（旧模型只有 first／last 二值，此处已放开）。
    /// </para>
    /// </summary>
    public string? Band { get; }

    /// <summary>
    /// 该成员的**段序声明**（有序段名；下标即段序，小者先执行；null＝单段方案）。
    /// <para>
    /// 段序来自旧 band 方案的枚举数值序（触发端／处理端／指令流三套，见 <c>BandSchemes</c>）：
    /// 成员按方案给自己的自有边挑一段，外来挂载边按 <see cref="MountEdge.Band"/> 挑另一段，
    /// 两者合起来给出成员的完整执行序。
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? Bands { get; }

    /// <summary>
    /// 该成员 handler 内部的**具名插入点**（声明序；空数组＝不声明任何缝）。
    /// <para>
    /// 包装器挂到 <c>(成员, 缝名)</c> 上（<see cref="WrapDeclaration.Seam"/>）；handler 内
    /// <c>ctx.SeamAsync(缝名)</c> 按该缝的包装器列表 LIFO 执行。未声明＝调用
    /// <see cref="Context.SeamAsync"/> 抛 <see cref="KeyNotFoundException"/>。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Seams { get; }

    /// <summary>是否声明了某具名缝（<see cref="Context.SeamAsync"/> 的 fail-fast 依据）。</summary>
    public bool DeclaresSeam(string name) => Contains(Seams, name);

    /// <summary>
    /// 段名 → 执行序下标（plan 为 null 或段名为 null 时＝0；段名不在方案里＝<see cref="ArgumentException"/>）。
    /// </summary>
    /// <exception cref="ArgumentException">段名未在 <see cref="Bands"/> 中声明。</exception>
    public int BandIndexOf(string? band)
    {
        if (band is null || Bands is null)
        {
            return 0;
        }

        for (var i = 0; i < Bands.Count; i++)
        {
            if (string.Equals(Bands[i], band, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new ArgumentException(
            $"段名 '{band}' 未在成员 '{Name}' 的 bands 方案中声明（已声明：{string.Join("／", Bands)}）。", nameof(band));
    }

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

    /// <summary>
    /// **成员级封死**（R2；缺省 false＝可覆写）：父逐个封死成员，资产层覆写＝装载期拒绝。
    /// <c>framework.</c> 前缀恒 sealed——由 <see cref="IsEffectivelySealed"/> 统一给出，不要求本字段为 true。
    /// </summary>
    public bool Sealed { get; }

    /// <summary>
    /// **成员级空位声明**（R39 裁决 5／8；null＝普通成员／纯编排成员）：链上声明过＝可填／可重填。
    /// 与 <see cref="HasHandler"/> 正交，四行判定表就是两个事实的存在性组合（D40）。
    /// </summary>
    public MemberSlotDeclaration? Slot { get; }

    /// <summary>该成员当前是否已有实现（资产层 <c>handler</c> 字段的存在性投影）。</summary>
    public bool HasHandler { get; }

    /// <summary>
    /// **未填空位**（D40 判定表第 1 行；S28／S32 的**唯一**成员层判据）。
    /// <para>装载期「是否可被 components 引用」与摊平期「是否必须实现」都只读它。</para>
    /// </summary>
    public bool IsUnfilledSlot => Slot is not null && !HasHandler;

    /// <summary>
    /// **守卫档**（R32；null＝不守卫）：模板成员声明，生成器把它合成进 csx 首行过滤代码；
    /// 摊平产物**不携带**本字段（守卫已烘进文本，D24／D25）。
    /// </summary>
    public HandlerGuard? Guard { get; }

    /// <summary>
    /// 「实际上不可覆写」：成员级 <see cref="Sealed"/> 或 <c>framework.</c> 保留名（恒 sealed，R2 第 4 条）。
    /// </summary>
    public bool IsEffectivelySealed => Sealed || ComponentDisposalPolicy.IsReservedMember(Name);

    /// <summary>是否具备某能力位。</summary>
    public bool Has(MemberVisibility flag) => (Visibility & flag) == flag;

    /// <summary>
    /// 覆写本成员（R3a：整段替换；可见性只能收紧）。
    /// <para>
    /// **授权门（R39 裁决 5，D39／D40；R2 已被修订）**：资产层参与的继承必须同时满足
    /// ①该成员在链上被声明为 slot（本对象的 <see cref="Slot"/> 非空）；
    /// ②该 slot 未被 <see cref="IsEffectivelySealed"/> 锁定。**可覆写性只由声明决定，不由实现状态决定**
    /// ——「已经有 handler 正文」不产生任何资格；也不再有任何资产级总闸。
    /// </para>
    /// <para>
    /// 引擎内部层间继承（<see cref="PrefabLayer.Kernel"/> ↔ <see cref="PrefabLayer.Game"/>）**豁免**，
    /// 既有 <c>engine ← engine.game</c>、<c>base ← unit/order/counter/hq</c> 的覆写行为逐条不变（R2 第 5、6 条）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">子声明的可见性放宽了父声明。</exception>
    /// <exception cref="PrefabLoadException">该成员不是空位／空位已 sealed／<c>framework.</c> 前缀。</exception>
    internal MemberDefinition Override(
        MemberDefinition child,
        PrefabLayer parentLayer,
        PrefabLayer childLayer,
        string parentId)
    {
        if ((child.Visibility & ~Visibility) != 0)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Visibility,
                $"成员 '{Name}' 的覆写放大了可见性（父＝{Visibility}，子＝{child.Visibility}）：继承只能收紧、不能放宽（R5）。");
        }

        if (!IsEngineInternalInheritance(parentLayer, childLayer))
        {
            if (IsEffectivelySealed)
            {
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"成员 '{Name}' 的空位被父资产 '{parentId}' 封死（"
                    + (Sealed ? "成员级 sealed" : "framework. 前缀恒 sealed")
                    + ")：sealed 锁定这个 slot，子不得再填（R39 裁决 5）。");
            }

            if (Slot is null)
            {
                // R39 裁决 5／D40：**可覆写性只由声明决定**——没有 slot 就是不可覆写，与实现状态无关。
                // 不再提示「请打开 authorize-moding」（该开关已废止）。
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"成员 '{Name}' 不是空位：父资产 '{parentId}' 没有在链上把它声明为 slot "
                    + "(slot <成员名>: handler)，因此不允许被覆写（R2 已被 R39 修订）。");
            }
        }

        // 守卫与 slot 沿继承链继承（R32／R39）：
        //  - 作者看不见 guard，子覆写「不重声明」＝沿用父的档，不是「取消守卫」；
        //  - slot 是「这里有一个空位」的声明，子填一次**不消灭**它（D39：填一次只改状态），否则
        //    「产物可再填」不成立。子显式重声明 slot 时类型必须相同（成员级只有 handler，天然相同）。
        return child.WithInheritedGuard(Guard).WithInheritedSlot(Slot);
    }

    /// <summary>
    /// 引擎内部层间继承（<see cref="PrefabLayer.Kernel"/>／<see cref="PrefabLayer.Game"/> 之间）——
    /// 授权判定对它们**整段豁免**：同一代码库内的层间继承不是「资产试图覆写另一份资产」（R2 第 6 条）。
    /// </summary>
    private static bool IsEngineInternalInheritance(PrefabLayer parentLayer, PrefabLayer childLayer)
        => IsEngineLayer(parentLayer) && IsEngineLayer(childLayer);

    private static bool IsEngineLayer(PrefabLayer layer) => layer is PrefabLayer.Kernel or PrefabLayer.Game;
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
