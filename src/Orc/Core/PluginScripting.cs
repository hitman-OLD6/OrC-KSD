namespace Orc.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 插件脚本求值（资产化 S3）：csx「注册期入口」的**接口倒置**（内核半段）——与既有 IScriptEvaluator 并列，
// **不改既有语义**。内核只定义契约，Roslyn 实现由 satellite 工程（Orc.Script）提供。
// 差别：既有 IScriptEvaluator 求值"触发器事件 handler"（需 ViewType、产出 Func<TView,Context,CancellationToken,Task>）；
// 本契约求值"插件注册入口"（无 ViewType、产出 Action<IPluginRegistrationContext>），令 csx 可注册
// 资产服务/判定器/效果 handler 等强类型注册项。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 插件脚本求值请求：源码 ＋ 入口名（默认 <c>Register</c>）＋ 超时 ＋ 缓存域。
/// </summary>
/// <param name="Source">脚本源码文本（csx）。</param>
/// <param name="EntryName">入口变量名（默认 <c>Register</c>）。</param>
/// <param name="TimeoutMs">求值/编译超时（毫秒；≤0＝用求值器默认）。</param>
/// <param name="CacheScope">缓存域（用于区分多版本同源的包；如 <c>{包GUID8}#{版本}</c>；null＝不区分）。</param>
public sealed record PluginScriptRequest(
    string Source,
    string EntryName = "Register",
    int TimeoutMs = 0,
    string? CacheScope = null);

/// <summary>包装器目标类别：插件声明改写哪一类注册条目的逻辑。</summary>
public enum WrapperTarget
{
    /// <summary>判定器（语义规则类包装）。</summary>
    Judicator = 0,

    /// <summary>资产服务（静态资产裁决/值域包装）。</summary>
    AssetService = 1,
}

/// <summary>
/// 插件注册上下文（受控面）：csx 注册入口经其完成受控注册——**复用转发**既有注册面
/// （<c>AssetServiceRegistry.Register</c> / <c>JudicatorRegistry.Register</c> / <c>SnapshotStore.RegisterHandler</c>），
/// 不造第二注册面。具体实现由宿主层（Orc.Packages）提供。
/// </summary>
public interface IPluginRegistrationContext
{
    /// <summary>当前插件的人类可读包 id。</summary>
    string PackageId { get; }

    /// <summary>当前插件的包 GUID 前 8 位（资产名继承用）。</summary>
    string PackageGuid8 { get; }

    /// <summary>注册资产服务（转发到 <see cref="AssetServiceRegistry.Register"/>）。</summary>
    void RegisterAssetService(string name, AssetService service);

    /// <summary>
    /// 注册判定器（转发到 <see cref="JudicatorRegistry.Register"/>）；返回条目等价句柄
    /// （供本条目的**同包内**其他注册项以句柄消费——如组合器注入子规则条目，对齐内置装配先例）。
    /// </summary>
    JudicatorRegistration RegisterJudicator(string name, Judicator judicator);

    /// <summary>注册效果 handler / 行为引用（转发到 <c>SnapshotStore.RegisterHandler</c>）。</summary>
    void RegisterHandler(string key, Delegate handler);

    /// <summary>声明本条目对某插件资产名的依赖（条目级依赖；承载"效果声明其依赖的插件的资产名"）。</summary>
    void DeclareDependency(string assetName);

    /// <summary>
    /// 读取**本包**已声明资产的文本内容（按包内短名；数据 JSON／csx 源文本）。
    /// 未声明该资产或不可读＝明确失败（fail-fast）；沙箱禁文件 IO，故数据须经本面进入脚本。
    /// </summary>
    string ReadAsset(string assetName);

    /// <summary>声明前置包装器（目标条目核心之前 LIFO 执行）。</summary>
    void DeclareBeforeWrapper(WrapperTarget target, string name, Action<object[]?> wrapper);

    /// <summary>声明后置包装器（目标条目核心之后 FIFO 执行）。</summary>
    void DeclareAfterWrapper(WrapperTarget target, string name, Func<object[]?, object[]?, object[]?> wrapper);

    /// <summary>声明替换包装器（只替换核心段；最后一个未注销者生效）。</summary>
    void DeclareReplaceWrapper(WrapperTarget target, string name, Func<object[]?, object[]?> wrapper);
}

/// <summary>
/// 插件脚本求值结果（结构化——不成败不抛异常）：成功携带注册动作，失败携带分类与消息。
/// 分类为开放字符串（compile/runtime/entry-missing/signature/sandbox/timeout），与既有沙箱口径对齐。
/// </summary>
public sealed class PluginScriptResult
{
    private PluginScriptResult(
        bool success,
        Action<IPluginRegistrationContext>? register,
        string? errorCategory,
        string? error)
    {
        Success = success;
        Register = register;
        ErrorCategory = errorCategory;
        Error = error;
    }

    /// <summary>是否成功。</summary>
    public bool Success { get; }

    /// <summary>注册动作（成功时非 null）：宿主以具体 <see cref="IPluginRegistrationContext"/> 调用之。</summary>
    public Action<IPluginRegistrationContext>? Register { get; }

    /// <summary>失败分类（成功＝null）。</summary>
    public string? ErrorCategory { get; }

    /// <summary>失败消息（成功＝null）。</summary>
    public string? Error { get; }

    /// <summary>构造成功结果。</summary>
    /// <exception cref="ArgumentNullException">register 为 null。</exception>
    public static PluginScriptResult Ok(Action<IPluginRegistrationContext> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        return new PluginScriptResult(true, register, null, null);
    }

    /// <summary>构造失败结果（不抛错——结构化失败是正常路径）。</summary>
    /// <exception cref="ArgumentNullException">category 为 null。</exception>
    public static PluginScriptResult Fail(string category, string error)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new PluginScriptResult(false, null, category, error);
    }
}

/// <summary>
/// 插件脚本求值器（内核定义、satellite 实现）：把 csx 源码的注册入口编译为可调用注册动作。
/// 契约：**不抛异常**（一切失败经 <see cref="PluginScriptResult.Fail"/> 结构化返回）；实现须遵守受控引用集与超时。
/// </summary>
public interface IPluginScriptEvaluator
{
    /// <summary>求值一个插件注册入口。</summary>
    /// <exception cref="ArgumentNullException">request 为 null。</exception>
    PluginScriptResult Evaluate(PluginScriptRequest request);
}
