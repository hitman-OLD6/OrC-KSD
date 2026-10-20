using Orc.Core;
using Orc.Game.Plugins;
using Orc.Packages;

namespace Orc.Plugins.BuiltIn;

/// <summary>
/// 内置插件**物理包**装载（资产化 S12b）：清单来自磁盘（<c>package/plugins/orc.builtin.core/manifest.json</c>），
/// 注册入口来自**本程序集代码**（内置判定器须访问游戏层内部类型，无法以数据/csx 表达——正是混合包"程序集逃生舱"场景）。
/// 稳定身份（GUID）与版本固定 ⇒ 资产名稳定。
/// </summary>
public static class BuiltInPluginPackage
{
    /// <summary>包 id。</summary>
    public const string PackageId = "orc.builtin.core";

    /// <summary>包目录名（<c>&lt;pluginsRoot&gt;/orc.builtin.core</c>）。</summary>
    public const string DirectoryName = "orc.builtin.core";

    /// <summary>包稳定身份（固定字面——跨版本不变）。</summary>
    public static readonly Guid PackageGuid = Guid.Parse("b0070000-0000-4000-8000-000000000001");

    /// <summary>UI 插件包 id（<c>orc.builtin.ui</c>；装载序声明用——纯 id，不引用该程序集）。</summary>
    public const string UiPackageId = "orc.builtin.ui";

    /// <summary>底层逻辑集包 id（<c>orc.foundation.actions</c>；装载序声明用）。</summary>
    public const string FoundationActionsPackageId = "orc.foundation.actions";

    /// <summary>
    /// **内置包注册清单**（S4）：为每个内置插件声明显式装载序（<c>LoadAfter[]</c>——纯包 ID、代码内、
    /// **不进 manifest**）。宿主启动期据此做三项断言（覆盖／无环／无未知包 ID）。
    /// </summary>
    public static IReadOnlyList<PackageLoadOrderEntry> LoadOrder { get; } =
    [
        new(PackageId, Array.Empty<string>()),
        new(UiPackageId, Array.Empty<string>()),
        new(FoundationActionsPackageId, [PackageId]),
    ];

    /// <summary>默认插件根（程序集输出目录下的 <c>package/plugins</c>）。</summary>
    public static string DefaultPluginsRoot
        => Path.Combine(AppContext.BaseDirectory, "package", "plugins");

    /// <summary>
    /// 由插件根装载内置插件包（清单读磁盘、脚本入口为本程序集代码）。
    /// </summary>
    /// <param name="pluginsRoot">插件根目录（缺省＝<see cref="DefaultPluginsRoot"/>）。</param>
    /// <exception cref="FileNotFoundException">清单缺失。</exception>
    /// <exception cref="InvalidOperationException">清单 id/kind/身份不符。</exception>
    /// <exception cref="FormatException">清单非法。</exception>
    public static LoadedPackage Load(string? pluginsRoot = null)
    {
        var directory = Path.Combine(pluginsRoot ?? DefaultPluginsRoot, DirectoryName);
        var manifestPath = Path.Combine(directory, PackageDiscovery.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"内置插件清单缺失：{manifestPath}", manifestPath);
        }

        var manifest = PackageManifest.Parse(File.ReadAllText(manifestPath));
        if (manifest.Kind != PackageKind.Plugin || !string.Equals(manifest.Id, PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"内置插件清单不符（期望 plugin/{PackageId}，实际 {manifest.Kind}/{manifest.Id}）。");
        }

        if (manifest.Guid != PackageGuid)
        {
            throw new InvalidOperationException(
                $"内置插件身份不符（期望 {PackageGuid}，实际 {manifest.Guid}）——包身份须稳定。");
        }

        // 合成“注册入口”资产（绑定代码入口；供诊断/审计使用）——两个相各一。
        var preDescriptor = new PackageAsset(
            "asm", "register", PackageDiscovery.ManifestFileName,
            AssetScope.Match, AssetActivationPhase.PreCommand);
        var preAsset = new LoadedAsset(
            preDescriptor,
            new AssetName(AssetLoadKind.Asm, "register", manifest.Guid8, new AssetVersion(1, 0, 0)),
            manifestPath);

        var postDescriptor = new PackageAsset(
            "asm", "register.postCommand", PackageDiscovery.ManifestFileName,
            AssetScope.Match, AssetActivationPhase.PostCommand);
        var postAsset = new LoadedAsset(
            postDescriptor,
            new AssetName(AssetLoadKind.Asm, "register.postCommand", manifest.Guid8, new AssetVersion(1, 0, 0)),
            manifestPath);

        var processDescriptor = new PackageAsset(
            "asm", "register.process", PackageDiscovery.ManifestFileName,
            AssetScope.Process, AssetActivationPhase.PreCommand);
        var processAsset = new LoadedAsset(
            processDescriptor,
            new AssetName(AssetLoadKind.Asm, "register.process", manifest.Guid8, new AssetVersion(1, 0, 0)),
            manifestPath);

        return new LoadedPackage(
            manifest,
            directory,
            new[] { preAsset, postAsset, processAsset },
            new[]
            {
                new LoadedPluginScript(preAsset, RegisterEntry),
                new LoadedPluginScript(postAsset, RegisterPostCommandEntry),
                new LoadedPluginScript(processAsset, _ => BuiltInPlugin.RegisterProcessDefaults()),
            });
    }

    /// <summary>构造仅含内置插件的资产目录快照。</summary>
    /// <exception cref="FileNotFoundException">清单缺失。</exception>
    public static AssetCatalogSnapshot CreateSnapshot(string? pluginsRoot = null)
        => AssetCatalogSnapshot.FromPackages(new[] { Load(pluginsRoot) });

    private static void RegisterEntry(IPluginRegistrationContext context)
    {
        if (context is not IMatchPluginContext matchContext)
        {
            throw new InvalidOperationException(
                "内置插件需要 IMatchPluginContext（对局服务面 IMatchServices）——请以对局装配路径激活。");
        }

        BuiltInPlugin.Register(matchContext);
    }

    private static void RegisterPostCommandEntry(IPluginRegistrationContext context)
    {
        if (context is not IMatchPluginContext matchContext)
        {
            throw new InvalidOperationException(
                "内置插件需要 IMatchPluginContext（对局绑定面 IMatchBindingServices）——请以对局装配路径激活。");
        }

        BuiltInPlugin.RegisterPostCommand(matchContext);
    }
}
