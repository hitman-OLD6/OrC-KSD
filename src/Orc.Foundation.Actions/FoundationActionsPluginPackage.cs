using Orc.Core;
using Orc.Packages;

namespace Orc.Foundation.Actions;

/// <summary>
/// 底层逻辑集的**物理插件包**（P5b）：清单来自磁盘
/// （<c>package/plugins/orc.foundation.actions/manifest.json</c>），注册入口来自本程序集代码
/// （动作实现须构造对局级触发器，无法以数据表达——混合包"程序集逃生舱"场景，与内置插件同构）。
/// <para>
/// 稳定身份（GUID）与版本固定 ⇒ 资产名稳定；依赖 <c>orc.builtin.core 1.*.*</c> ⇒ 解析期保证 core 先装。
/// 清单 <c>assets: []</c> 合法（资产由<see cref="Load"/> 合成一个代码入口资产）。
/// </para>
/// </summary>
public static class FoundationActionsPluginPackage
{
    /// <summary>包 id。</summary>
    public const string PackageId = "orc.foundation.actions";

    /// <summary>包目录名（<c>&lt;pluginsRoot&gt;/orc.foundation.actions</c>）。</summary>
    public const string DirectoryName = "orc.foundation.actions";

    /// <summary>包稳定身份（固定字面——跨版本不变）。</summary>
    public static readonly Guid PackageGuid = Guid.Parse("7f3c2a10-9b64-4d8e-a5c1-6e0f2b7d4a93");

    /// <summary>默认插件根（程序集输出目录下的 <c>package/plugins</c>）。</summary>
    public static string DefaultPluginsRoot
        => Path.Combine(AppContext.BaseDirectory, "package", "plugins");

    /// <summary>由插件根装载底层逻辑集包（清单读磁盘、注册入口为本程序集代码）。</summary>
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
            throw new FileNotFoundException($"底层逻辑集清单缺失：{manifestPath}", manifestPath);
        }

        var manifest = PackageManifest.Parse(File.ReadAllText(manifestPath));
        if (manifest.Kind != PackageKind.Plugin || !string.Equals(manifest.Id, PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"底层逻辑集清单不符（期望 plugin/{PackageId}，实际 {manifest.Kind}/{manifest.Id}）。");
        }

        if (manifest.Guid != PackageGuid)
        {
            throw new InvalidOperationException(
                $"底层逻辑集身份不符（期望 {PackageGuid}，实际 {manifest.Guid}）——包身份须稳定。");
        }

        // 合成“注册入口”资产（绑定代码入口；供诊断/审计使用）。
        var descriptor = new PackageAsset(
            "asm", "register.postCommand", PackageDiscovery.ManifestFileName,
            AssetScope.Match, AssetActivationPhase.PostCommand);
        var asset = new LoadedAsset(
            descriptor,
            new AssetName(AssetLoadKind.Asm, "register.postCommand", manifest.Guid8, manifest.Version),
            manifestPath);

        return new LoadedPackage(
            manifest,
            directory,
            new[] { asset },
            new[] { new LoadedPluginScript(asset, _ => FoundationActions.Register()) });
    }
}
