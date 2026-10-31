namespace Orc.Core.Model;

/// <summary>
/// 预制体装载期错误分类（与 <see cref="ScriptErrorCategories"/> 对齐：静态检查／编译／签名属于脚本面的三类，
/// 其余为本模型引入的结构面分类）。
/// </summary>
public static class PrefabErrorCategories
{
    /// <summary>结构错误（继承环、重复 id、非法字段声明等）。</summary>
    public const string Structure = "structure";

    /// <summary>寻址错误（地址语法非法、目标成员不存在）。</summary>
    public const string Addressing = "addressing";

    /// <summary>可见性错误（越界挂载、子放大父可见性）。</summary>
    public const string Visibility = "visibility";

    /// <summary>视图错误（字段类型超出封闭集、字段名与私有字段撞名、必填声明非法）。</summary>
    public const string View = "view";

    /// <summary>绑定错误（csx 里出现的视图／owner 字段未声明或写入只读字段，S5 第 8d 条）。</summary>
    public const string Binding = "binding";

    /// <summary>编译错误（csx 编译失败）。</summary>
    public const string Compile = "compile";

    /// <summary>签名错误（csx 入口签名不符）。</summary>
    public const string Signature = "signature";

    /// <summary>混用错误（挂载声明携带相位、包装声明引入新步骤等，R1b／A-2）。</summary>
    public const string Channel = "channel";

    /// <summary>时机错误（无意义挂载：<c>active-when: placed</c> 的组件挂加载期信号，S3 第 4c 条）。</summary>
    public const string Timing = "timing";

    /// <summary>
    /// 能力缺失（**环境未装配 <c>IHandlerDslCompiler</c>**，D6／D14）：专用于「这台机器上没有编译器」。
    /// <para>
    /// **不复用 <see cref="Compile"/>**——「编译失败了」与「这台机器上没有编译器」对作者是完全不同的指引
    /// （前者改脚本、后者装东西／换环境）；混在一类里作者会照着编译错误去改一个本来没问题的 DSL。
    /// </para>
    /// <para>这是**能力依赖**而非专项禁令：没有编译器就不能编译，等同于「没有 Roslyn 就编译不了 csx」（R9 不冲突）。</para>
    /// </summary>
    public const string Capability = "capability";
}

/// <summary>
/// 装载期错误（一条）：分类＋定位（作用域#成员名／预制体 id）＋消息＋可选缺失字段名清单。
/// </summary>
/// <param name="Category">错误分类（见 <see cref="PrefabErrorCategories"/>）。</param>
/// <param name="Target">出错对象（地址、预制体 id、成员名）。</param>
/// <param name="Message">人类可读原因。</param>
/// <param name="MissingFields">缺失字段名清单（仅视图包含校验等场景）。</param>
/// <param name="Code">
/// 图校验错误码（**仅 DSL 链填**，取值域＝契约包 <c>contracts/handler-dsl/v1/SPEC.md</c> §14.1 的封闭 13 码）；
/// csx 链维持现状——那四类仍以 <see cref="Category"/> 身份出现（D14：不要为了让字段整齐去搬它们的既有位置）。
/// 可空、默认 null，既有消费者零改动。
/// </param>
/// <param name="NodePath">
/// DSL **节点路径**（如 <c>actions[2].then[0]</c>、字段级 <c>actions[0].else</c>）。
/// **只在草案装载期有值**；产物装载期恒 null（产物里没有 DSL，R28）。可空、默认 null。
/// </param>
public sealed record PrefabLoadError(
    string Category,
    string Target,
    string Message,
    IReadOnlyList<string>? MissingFields = null,
    string? Code = null,
    string? NodePath = null);

/// <summary>
/// 装载期错误集合（**收集齐全部失败项**，不是遇错即停——S14 卡包原子性要求在批次 3 起按整包汇总，
/// 本模型先提供"可汇总"的报告形态）。
/// </summary>
public sealed class PrefabLoadReport
{
    private readonly List<PrefabLoadError> _errors = new();

    /// <summary>是否无错误。</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>全部错误（按登记序）。</summary>
    public IReadOnlyList<PrefabLoadError> Errors => _errors;

    /// <summary>登记一条错误。</summary>
    public void Add(PrefabLoadError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _errors.Add(error);
    }

    /// <summary>登记一条错误（分类＋定位＋消息）。</summary>
    public void Add(string category, string target, string message)
        => _errors.Add(new PrefabLoadError(category, target, message));

    /// <summary>有错误则抛出（fail-fast 的装载入口使用）；错误消息列出全部失败项。</summary>
    /// <exception cref="PrefabLoadException">报告含错误。</exception>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new PrefabLoadException(_errors);
        }
    }
}

/// <summary>
/// 预制体装载期异常（fail-fast）：装载期校验失败＝拒绝装载（不进入执行期），异常携带结构化错误集合。
/// 与旧路径的"运行时装载层＝隔离"口径相反（S5 第 9 条推翻了那一层）：新模型不降级为"成员缺失"。
/// </summary>
public sealed class PrefabLoadException : Exception
{
    /// <summary>以单条错误构造。</summary>
    public PrefabLoadException(string category, string message)
        : base(message)
    {
        Category = category;
        Errors = new[] { new PrefabLoadError(category, string.Empty, message) };
    }

    /// <summary>以错误集合构造（消息＝全部失败项的逐行拼接）。</summary>
    public PrefabLoadException(IReadOnlyList<PrefabLoadError> errors)
        : base(BuildMessage(errors))
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
        Category = errors.Count > 0 ? errors[0].Category : PrefabErrorCategories.Structure;
    }

    /// <summary>首个错误的分类。</summary>
    public string Category { get; }

    /// <summary>全部失败项。</summary>
    public IReadOnlyList<PrefabLoadError> Errors { get; }

    private static string BuildMessage(IReadOnlyList<PrefabLoadError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count == 0
            ? "预制体装载失败。"
            : "预制体装载失败（共 " + errors.Count + " 项）：" + Environment.NewLine
              + string.Join(Environment.NewLine, errors.Select(static e => $"  [{e.Category}] {e.Target}：{e.Message}"));
    }
}
