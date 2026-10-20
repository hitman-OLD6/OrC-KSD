using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Loading;

namespace Orc.Mcp;

/// <summary>
/// 服务层对**后端编辑器**（S8）与**本局加载台账**（S9）的持有面。
/// <para>
/// 分层约定（S9 硬要求）：
/// <list type="bullet">
///   <item>编辑器表达力的唯一来源是 <see cref="AssetEditor"/>（<c>Orc.Packages</c>）；本层只做**持有与包装**，
///     工具处理器直接转调其能力方法（工具与进程内 API 走同一后端组件，不复制逻辑）。</item>
///   <item>策略＝宿主注入的**单根**实现：沙盒模式用 <see cref="Sandbox.SandboxPathGuard"/>（授权根＝沙盒根，
///     故 <c>&lt;沙盒根&gt;/pack.tmp</c> 的直接父目录命中授权根）。</item>
///   <item>报告模型只在 <c>Orc.Packages</c> 存在一份（<see cref="CardLoadReport"/>／<see cref="CardLoadLedger"/>）；
///     本层不复制任何报告类型。</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class McpService
{
    /// <summary>编辑器专用资产注册面（进程级；<c>editor.reload</c> 的计算输入）。</summary>
    public AssetRegistry EditorRegistry { get; } = new();

    /// <summary>
    /// 后端编辑器（R9/R11/R13）：策略＝沙盒单根守卫；可编辑包根＝沙盒根（`pack` 与 `pack.tmp` 均为其子项）。
    /// <para>非沙盒模式同样持有实例（零 IO 副作用），但 `editor.*` 工具**不登记**，外部不可达。</para>
    /// </summary>
    public AssetEditor Editor { get; }

    /// <summary>
    /// 活跃对局的卡组装载（<c>null</c>＝无活跃对局，或该对局走代码内定义集直注册路径＝沙盒局）。
    /// 诊断面（<c>diag.card_load</c>）经此读取 <see cref="MatchCardAssembly.Ledger"/>。
    /// </summary>
    public MatchCardAssembly? CardAssembly { get; internal set; }

    private AssetEditor CreateEditor()
        => new(
            EditorRegistry,
            Sandbox.Guard,
            new AssetEditorOptions
            {
                PackageRoots = new[] { Sandbox.Guard.Root },
                Loader = new AssetLoader(EditorRegistry),
            });
}
