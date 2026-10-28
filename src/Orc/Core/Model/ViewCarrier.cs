using System.Dynamic;

namespace Orc.Core.Model;

/// <summary>
/// 承载视图（View Carrier）——**全仓唯一的通用视图运行期类型**（R1a 作用域澄清）。
/// <para>
/// 承接**全部**视图声明：不为每个数据形状新增 C# 类，形状由 <see cref="ViewSpec"/>（文本字段清单）表达，
/// 本类按名从数据面投影并实施读写闸门。csx 入口签名因此统一为
/// <c>Func&lt;dynamic, Context, CancellationToken, Task&gt;</c>（<c>dynamic</c> 在运行期即 <c>object</c>）。
/// </para>
/// <para>
/// **名字空间（W-14 定稿）**：只含视图声明的公共字段（数据面），**不含**组件私有字段——
/// 私有字段唯一通道是 <c>owner.&lt;Name&gt;</c>；写 <c>view.&lt;私有字段&gt;</c> 会被静态检查判 <c>binding</c>，
/// 运行期还会被本类的 setter 闸门拒绝。
/// </para>
/// <para>
/// **写回（R1c／S6）**：只有 <c>view.&lt;Name&gt;</c> 通道可写，且写入直接落**宿主数据面**（同一执行链的后续步骤可见）；
/// <c>owner.&lt;Name&gt;</c> 的写入是实体状态写入，不产生数据面写回。
/// </para>
/// <para>
/// **双闸门**（S5 第 8 条）：装载期静态语法检查（Roslyn，<c>Orc.Script</c>）＋ 本类的运行期 setter 闸门（兜底）。
/// 后者的存在理由：静态检查"未识别形态＝放行"（W-16），放行的那部分只由本闸门兜住。
/// </para>
/// </summary>
public sealed class ViewCarrier : DynamicObject
{
    private readonly ViewSpec _spec;
    private readonly Dictionary<string, object?> _plane;
    private bool _sealed;

    private ViewCarrier(
        ViewSpec spec, Dictionary<string, object?> plane, object owner, object? host, string memberName)
    {
        _spec = spec;
        _plane = plane;
        Owner = owner;
        Host = host;
        MemberName = memberName;

        // 绑定即校验（A-1）：字段名／类型／contains-host 已在装载期判完；此处只剩"数据面值是否齐备"，
        // 必填缺失＝首次执行 fail-fast（不返回半可用视图）。
        foreach (var field in spec.Fields)
        {
            _ = spec.Project(field, plane);
        }
    }

    /// <summary>所属实体（框架回填，**不来自数据面**；R4）。</summary>
    public object Owner { get; }

    /// <summary>组件宿主（卡牌／HQ；非组件实体＝null）。取自 owner 的宿主引用，**不来自数据面**。</summary>
    public object? Host { get; }

    /// <summary>正在执行的成员名（诊断用）。</summary>
    public string MemberName { get; }

    /// <summary>本视图的字段声明。</summary>
    public ViewSpec Spec => _spec;

    /// <summary>创建并绑定视图（框架调用；执行会话开始时一次）。</summary>
    /// <exception cref="ArgumentNullException">ctx／owner 为 null。</exception>
    /// <exception cref="KeyNotFoundException">必填字段缺失。</exception>
    /// <exception cref="InvalidCastException">数据面值类型与声明不符。</exception>
    internal static ViewCarrier Bind(ViewSpec spec, Context ctx, object owner, object? host, string memberName)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(owner);
        return new ViewCarrier(spec, ctx.Data, owner, host, memberName);
    }

    /// <summary>
    /// 按名读取字段值（C# handler 的强类型读取入口；csx 用 <c>view.&lt;Name&gt;</c> 走动态绑定）。
    /// </summary>
    /// <exception cref="PermissionDeniedException">字段未在视图声明中（名字空间只含视图声明字段）。</exception>
    /// <exception cref="KeyNotFoundException">必填字段缺失。</exception>
    public object? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var field = _spec.Find(name)
            ?? throw new PermissionDeniedException(
                $"视图字段 '{name}' 未在视图声明中（承载视图的名字空间只含视图声明字段；私有字段请走 owner.{name}）。");
        return _spec.Project(field, _plane);
    }

    /// <summary>按名读取并转成指定类型（数字归一）。</summary>
    public T Get<T>(string name)
    {
        var value = Get(name);
        if (value is null)
        {
            throw new KeyNotFoundException($"视图字段 '{name}' 的值为 null，无法转成 {typeof(T).Name}。");
        }

        if (value is T typed)
        {
            return typed;
        }

        if (ViewFieldTypes.IsNumber(value) && ViewFieldTypes.IsNumber(default(T)!))
        {
            return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new InvalidCastException(
            $"视图字段 '{name}' 的值类型 {value.GetType().Name} 无法转成 {typeof(T).Name}。");
    }

    /// <summary>尝试读取（字段未声明＝false，不抛；用于可选字段探测）。</summary>
    public bool TryGet(string name, out object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var field = _spec.Find(name);
        if (field is null || (!_plane.ContainsKey(name) && field.Required))
        {
            value = null;
            return false;
        }

        value = _spec.Project(field, _plane);
        return true;
    }

    /// <summary>
    /// 写入字段并**写回宿主数据面**（R1c／S6）。
    /// </summary>
    /// <exception cref="PermissionDeniedException">字段未声明，或未声明为可写（写只读字段＝拒绝）。</exception>
    /// <exception cref="InvalidOperationException">视图已封（执行会话结束后再写）。</exception>
    /// <exception cref="InvalidCastException">值类型不符合声明类型。</exception>
    public void Set(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_sealed)
        {
            throw new InvalidOperationException($"视图已封：字段 '{name}' 的写入被拒绝（执行会话已结束）。");
        }

        var field = _spec.Find(name)
            ?? throw new PermissionDeniedException(
                $"视图字段 '{name}' 未在视图声明中（写入被拒绝）。");
        if (!field.Writable)
        {
            throw new PermissionDeniedException(
                $"视图字段 '{name}' 为只读（写入被拒绝；如确需写回，请在视图声明中标记为可写）。");
        }

        if (value is not null && !ViewFieldTypes.Accepts(field.Type, value))
        {
            throw new InvalidCastException(
                $"视图字段 '{name}' 声明为 {ViewFieldTypes.Format(field.Type)}，不接受 {value.GetType().Name}。");
        }

        if (value is null && field.Required)
        {
            throw new InvalidCastException($"视图字段 '{name}' 为必填，不接受 null。");
        }

        _plane[name] = value;
    }

    /// <summary>动态读取（csx：<c>view.&lt;Name&gt;</c>）。</summary>
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        ArgumentNullException.ThrowIfNull(binder);
        result = Get(binder.Name);
        return true;
    }

    /// <summary>动态写入（csx：<c>view.&lt;Name&gt; = …</c>）：运行期写入闸门。</summary>
    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        ArgumentNullException.ThrowIfNull(binder);
        Set(binder.Name, value);
        return true;
    }

    /// <summary>可动态访问的成员名（声明字段＋内建 Owner／Host；诊断用）。</summary>
    public override IEnumerable<string> GetDynamicMemberNames()
        => _spec.Names.Concat(new[] { nameof(Owner), nameof(Host) });

    /// <summary>封闭视图（执行会话结束时由框架调用；此后写入被拒绝）。</summary>
    internal void Seal() => _sealed = true;
}
