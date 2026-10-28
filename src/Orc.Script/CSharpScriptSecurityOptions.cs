namespace Orc.Script;

/// <summary>
/// 脚本安全选项（S-C6；等价于 SecureCSharpEval 的「白/黑名单 + 关键字过滤 + 超时」模型）：
/// 允许导入、允许引用的程序集、关键字屏蔽、超时。
/// 定位：**尽力而为**的弱沙箱（同进程、非进程隔离）——只提升审查/透明性，不构成安全边界；
/// 真正的强隔离须进程级（独立进程 + OS 资源限制 + 禁网 + 最小权限）。
/// </summary>
public sealed class CSharpScriptSecurityOptions
{
    /// <summary>默认允许的程序集简单名（引用白名单；实际取自已加载程序集）。</summary>
    /// <remarks>
    /// <c>Microsoft.CSharp</c> 与 <c>System.Linq.Expressions</c> 为编排实体化（S5／W-15）**显式加入**：
    /// 承载视图以 <c>dynamic</c> 暴露（<c>Func&lt;dynamic, Context, CancellationToken, Task&gt;</c>），
    /// <c>dynamic</c> 既需要 DLR 绑定器程序集（<c>Microsoft.CSharp</c>），也需要
    /// <c>System.Runtime.CompilerServices.DynamicAttribute</c> 所在的 <c>System.Linq.Expressions</c>。
    /// 判据（记录，避免以后被当作安全回退）：它只提供绑定能力，**不扩大类型可见面**——白名单内的
    /// <c>Orc</c>／<c>Orc.Game</c> 类型本就可直接访问其公开成员；关键字过滤是文本级过滤，<c>dynamic</c> 不绕过它；
    /// 因此实际增量只是"编译期成员检查"，而这项检查已按 R1a 换成**装载期静态语法检查**（<see cref="ViewBindingAnalyzer"/>）。
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultAllowedAssemblies = new[]
    {
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Collections",
        "System.Linq",
        "System.Text",
        "System.Threading.Tasks",
        "Microsoft.CSharp",
        "System.Linq.Expressions",
        "Orc",
        "Orc.Game",
    };

    /// <summary>
    /// 默认允许的命名空间导入。
    /// 说明：**不**预置 `Orc.Game.*`——`using` 不存在的命名空间会导致编译失败，
    /// 而脚本宿主未必加载游戏层程序集（如纯内核/脚本测试环境）；需要游戏层 API 的宿主
    /// 经 <see cref="AllowedImports"/> 自行追加（或模板使用全限定类型名）。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAllowedImports = new[]
    {
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Text",
        "System.Threading",
        "System.Threading.Tasks",
        "Orc.Core",
        "Orc.Cards",
    };

    /// <summary>默认屏蔽的关键字/片段（文本级过滤；可被绕过，故仅作尽力而为）。</summary>
    public static readonly IReadOnlyList<string> DefaultBlockedKeywords = new[]
    {
        "unsafe",
        "fixed",
        "stackalloc",
        "Process",
        "File",
        "Directory",
        "Registry",
        "Socket",
        "WebClient",
        "HttpClient",
        "System.IO",
        "System.Net",
        "System.Reflection",
        "System.Diagnostics",
        "Assembly",
        "Activator",
        "DllImport",
        "Environment",
        "#load",
        "#r ",
    };

    /// <summary>允许导入的命名空间（空＝用默认）。</summary>
    public IReadOnlyList<string> AllowedImports { get; init; } = DefaultAllowedImports;

    /// <summary>允许引用的程序集简单名（空＝用默认）。</summary>
    public IReadOnlyList<string> AllowedAssemblies { get; init; } = DefaultAllowedAssemblies;

    /// <summary>屏蔽的关键字/片段（空＝用默认）。</summary>
    public IReadOnlyList<string> BlockedKeywords { get; init; } = DefaultBlockedKeywords;

    /// <summary>默认执行超时（毫秒）；请求可覆盖。</summary>
    public int TimeoutMs { get; init; } = 5000;
}
