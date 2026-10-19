using Orc.Foundation.Actions;
using Orc.Game.Plugins;
using Orc.Packages;
using Orc.Plugins.BuiltIn;

namespace Orc.Game.Tests;

/// <summary>
/// 测试宿主（资产化 S12c）：把**内置插件包**（<c>orc.builtin.core</c>）装载为对局包激活器，
/// 供测试建局时注入——替代原有的"内置注册段硬编码"（<c>Match</c> 的 <c>null</c> 激活器＝空目录）。
/// </summary>
public static class TestHost
{
    /// <summary>内置插件激活器（进程内共享一份；快照不可变 ⇒ 各对局注册表仍逐局独立）。</summary>
    public static readonly IMatchPackageActivator Activator =
        new SnapshotPackageActivator(BuiltInPluginPackage.CreateSnapshot());
}

/// <summary>
/// 测试程序集模块初始化（批次 2）：强制加载内置插件程序集并装配其**进程级**资产（词条），
/// 使用例（含不建对局、只用词条注册面的用例）与迁移前静态构造时序等价。
/// <para>
/// P5b：动作实现集是**进程级单一真源**（R17），在此注册一次；未注册时动作目录装配即 fail-fast。
/// </para>
/// </summary>
internal static class TestModuleInitializer
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();
    }
}
