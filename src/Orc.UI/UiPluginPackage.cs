using Orc.Core;
using Orc.Game.Plugins;
using Orc.Packages;

namespace Orc.UI;

/// <summary>
/// 内置 UI 插件**物理包**（S6·D4）：清单来自磁盘（<c>package/plugins/orc.builtin.ui/manifest.json</c>），
/// 注册入口来自**本程序集代码**（"合成 asm 描述符 + 代码入口"——照抄 <c>BuiltInPluginPackage</c> 先例，
/// 从而绕开 csx 沙箱白名单与 asm 加载器缺口）。稳定身份与版本固定 ⇒ 资产名稳定。
/// </summary>
public static class UiPluginPackage
{
    /// <summary>包 id。</summary>
    public const string PackageId = "orc.builtin.ui";

    /// <summary>包目录名。</summary>
    public const string DirectoryName = "orc.builtin.ui";

    /// <summary>包稳定身份（固定字面——跨版本不变）。</summary>
    public static readonly Guid PackageGuid = Guid.Parse("b0070000-0000-4000-8000-000000000002");

    /// <summary>默认插件根（程序集输出目录下的 <c>package/plugins</c>）。</summary>
    public static string DefaultPluginsRoot => Path.Combine(AppContext.BaseDirectory, "package", "plugins");

    /// <summary>装载本包（清单读磁盘、脚本入口为本程序集代码）。</summary>
    /// <exception cref="FileNotFoundException">清单缺失。</exception>
    /// <exception cref="InvalidOperationException">清单 id/kind/身份不符。</exception>
    public static LoadedPackage Load(string? pluginsRoot = null)
    {
        var directory = Path.Combine(pluginsRoot ?? DefaultPluginsRoot, DirectoryName);
        var manifestPath = Path.Combine(directory, PackageDiscovery.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"UI 插件清单缺失：{manifestPath}", manifestPath);
        }

        var manifest = PackageManifest.Parse(File.ReadAllText(manifestPath));
        if (manifest.Kind != PackageKind.Plugin || !string.Equals(manifest.Id, PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"UI 插件清单不符（期望 plugin/{PackageId}，实际 {manifest.Kind}/{manifest.Id}）。");
        }

        if (manifest.Guid != PackageGuid)
        {
            throw new InvalidOperationException(
                $"UI 插件身份不符（期望 {PackageGuid}，实际 {manifest.Guid}）——包身份须稳定。");
        }

        var processDescriptor = new PackageAsset(
            "asm", "register.process", PackageDiscovery.ManifestFileName,
            AssetScope.Process, AssetActivationPhase.PreCommand);
        var processAsset = new LoadedAsset(
            processDescriptor,
            new AssetName(AssetLoadKind.Asm, "register.process", manifest.Guid8, new AssetVersion(1, 0, 0)),
            manifestPath);

        var matchDescriptor = new PackageAsset(
            "asm", "register.match", PackageDiscovery.ManifestFileName,
            AssetScope.Match, AssetActivationPhase.PreCommand);
        var matchAsset = new LoadedAsset(
            matchDescriptor,
            new AssetName(AssetLoadKind.Asm, "register.match", manifest.Guid8, new AssetVersion(1, 0, 0)),
            manifestPath);

        return new LoadedPackage(
            manifest,
            directory,
            new[] { processAsset, matchAsset },
            new[]
            {
                new LoadedPluginScript(processAsset, RegisterProcessEntry),
                new LoadedPluginScript(matchAsset, RegisterMatchEntry),
            });
    }

    /// <summary>本包（单元素集）——供 <c>EngineHost.FromDiscovery(..., extraPackages)</c> 注入。</summary>
    public static IReadOnlyList<LoadedPackage> Packages(string? pluginsRoot = null) => [Load(pluginsRoot)];

    /// <summary>`process` 相入口：注册卡包生命周期观察者（驱动卡包词条加载）。</summary>
    private static void RegisterProcessEntry(IPluginRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is IPackageLifecycleContext lifecycle)
        {
            lifecycle.Lifecycle?.RegisterObserver(new CardPackKeywordLoader());
        }
    }

    /// <summary>`match/preCommand` 相入口：注册投影提供者工厂。</summary>
    private static void RegisterMatchEntry(IPluginRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not IMatchPluginContext match)
        {
            throw new InvalidOperationException("UI 插件需要 IMatchPluginContext（对局投影注册面）。");
        }

        var ui = match.Ui
            ?? throw new InvalidOperationException("UI 插件需要对局投影服务面（IUiPluginServices）——请以对局装配路径激活。");
        ui.Projections.Register(new UiProjectionProviderFactory());
    }
}
