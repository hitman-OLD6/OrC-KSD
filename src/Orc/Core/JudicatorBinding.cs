namespace Orc.Core;

/// <summary>
/// 判定器绑定（J2）：触发器「验证点」的判定器锚——统一承载两条装配解析路径：
/// ①对局路径＝注册表条目等价句柄（<see cref="FromRegistration"/>——经统一解析点调用；moding 改写全局生效、注销回退）；
/// ②独立构造路径＝内置默认判定器实例（<see cref="FromStandalone"/>——无注册表、无改写通道；构造即可用、零配置）。
/// 由绑定调用点（卡/管理器装配面）经「按名解析」（对局＝注册表解析；独立＝内置默认）获得后，
/// 经 <see cref="Trigger{TView}.BindValidation"/> 固定到触发器（装配期一次建立、生命周期内不可变）。
/// 仅由工厂创建；不含可变状态。
/// </summary>
public sealed class JudicatorBinding
{
    private readonly JudicatorRegistration? _registration;
    private readonly Judicator? _standalone;

    private JudicatorBinding(string name, JudicatorRegistration? registration, Judicator? standalone)
    {
        Name = name;
        _registration = registration;
        _standalone = standalone;
    }

    /// <summary>
    /// 创建对局路径绑定（注册表条目锚）：经注册动作所得或按名解析所得的条目等价句柄
    /// （同一可寻址锚——两通道等效；moding 改写经该条目生效）。
    /// </summary>
    /// <param name="registration">条目等价句柄（注册所得或按名解析所得）。</param>
    /// <exception cref="ArgumentNullException">registration 为 null。</exception>
    public static JudicatorBinding FromRegistration(JudicatorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return new JudicatorBinding(registration.Name, registration, standalone: null);
    }

    /// <summary>
    /// 创建独立构造路径绑定（内置默认判定器实例锚）：无注册表、无改写通道——
    /// 「默认判定器无条件可绑定」（构造即可用、零配置；独立构造路径的缺省承载）。
    /// </summary>
    /// <param name="name">判定器名（按名绑定时的解析名——审计/定位用；受控常量集合为唯一称谓来源）。</param>
    /// <param name="judicator">内置默认判定器实例。</param>
    /// <exception cref="ArgumentException">name 为 null 或空白。</exception>
    /// <exception cref="ArgumentNullException">judicator 为 null。</exception>
    public static JudicatorBinding FromStandalone(string name, Judicator judicator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(judicator);
        return new JudicatorBinding(name, registration: null, judicator);
    }

    /// <summary>判定器名（按名绑定时的解析名；如「validation.cost.check」）。</summary>
    public string Name { get; }

    /// <summary>
    /// 验证调用（统一面调用＋输出契约解释）：输入＝refs＋subject；输出＝<see cref="ValidationVerdict"/>。
    /// 对局路径经条目统一解析点（每次调用解析栈顶一次并固定——moding 生效＝替换逻辑）；独立路径直接调用实例主方法。
    /// 输出契约不符（统一面输出非 <c>[ValidationVerdict]</c> 单元素）＝明确异常（fail-fast 族——运行期由触发路径契约兜底承接）。
    /// </summary>
    public ValidationVerdict Validate(IReadOnlyList<Ref<Entity>> refs, Ref<Entity>? subject, int? playerIndex = null)
        => InvokeValidation(refs, subject, playerIndex);

    /// <summary>内部调用面（与 <see cref="Validate"/> 同一实现；内核侧沿用旧名）。</summary>
    internal ValidationVerdict InvokeValidation(IReadOnlyList<Ref<Entity>> refs, Ref<Entity>? subject, int? playerIndex = null)
    {
        var args = new object[] { refs, subject! }; // subject 可为 null（合法载荷值：无被判定对象——数组元素运行时承载 null）
        var raw = _registration is not null ? _registration.Invoke(args, playerIndex) : _standalone!.Invoke(args);
        if (raw is { Length: 1 } && raw[0] is ValidationVerdict verdict)
        {
            return verdict;
        }

        throw new InvalidOperationException(
            $"验证判定器 '{Name}' 输出契约不符：期望 object[]{{ValidationVerdict}}（1 元素），实际 {Describe(raw)}——fail-fast。");
    }

    private static string Describe(object[]? raw)
        => raw is null
            ? "null（无输出）"
            : $"长度 {raw.Length}（首元素类型 {(raw.Length == 0 ? "—" : raw[0]?.GetType().Name ?? "null")}）";
}
