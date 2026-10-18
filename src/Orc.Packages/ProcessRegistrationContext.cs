using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Plugins;

namespace Orc.Packages;

/// <summary>
/// 全局（进程级）注册面实现（宿主层；P11 甲）：把 <see cref="IGlobalRegistrationServices"/> 的调用
/// **转发**到既有静态注册表（词条／数据组件），不造第二注册面。
/// </summary>
public sealed class GlobalRegistrationServices : IGlobalRegistrationServices
{
    /// <inheritdoc />
    public void RegisterKeyword(string keyword, Func<Card, int?, KeywordComponent> factory, bool isBattleKeyword = false)
        => KeywordRegistry.Register(keyword, factory, isBattleKeyword);

    /// <inheritdoc />
    public void DeclareKeywordEffectBinding(string keyword, KeywordEffectBinding binding)
        => KeywordRegistry.DeclareEffectBindings(keyword, new[] { binding });

    /// <inheritdoc />
    public void RegisterCardComponent(CardComponentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        CardComponentRegistry.Register(
            entry.Name, entry.Phase, entry.Loader, entry.Reader, entry.IsBuiltIn, entry.Writer);
    }
}

/// <summary>
/// 全局（进程级）插件上下文（P11 甲）：包身份 ＋ 全局注册面 ＋ 本包数据读取。
/// 对局级注册（判定器／资产服务／效果 handler／moding）在本相＝**明确拒绝**（fail-fast——配置错误）。
/// </summary>
public sealed class GlobalRegistrationContext : IGlobalPluginContext, IPackageLifecycleContext
{
    private readonly LoadedPackage _package;

    /// <summary>创建全局上下文。</summary>
    /// <exception cref="ArgumentNullException">package 或 services 为 null。</exception>
    public GlobalRegistrationContext(
        LoadedPackage package,
        IGlobalRegistrationServices services,
        IPackageLifecycleServices? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(services);
        _package = package;
        Global = services;
        Lifecycle = lifecycle;
    }

    /// <inheritdoc />
    public IPackageLifecycleServices? Lifecycle { get; }

    /// <inheritdoc />
    public string PackageId => _package.Manifest.Id;

    /// <inheritdoc />
    public string PackageGuid8 => _package.Manifest.Guid8;

    /// <inheritdoc />
    public IGlobalRegistrationServices? Global { get; }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">全局相不支持对局级注册（配置错误）。</exception>
    public JudicatorRegistration RegisterJudicator(string name, Judicator judicator)
        => throw new InvalidOperationException(
            "全局（进程级）脚本不得注册对局级资产（判定器）——请改用 scope: match。");

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">全局相不支持对局级注册（配置错误）。</exception>
    public void RegisterAssetService(string name, AssetService service)
        => throw new InvalidOperationException(
            "全局（进程级）脚本不得注册对局级资产（资产服务）——请改用 scope: match。");

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">全局相不支持对局级注册（配置错误）。</exception>
    public void RegisterHandler(string key, Delegate handler)
        => throw new InvalidOperationException(
            "全局（进程级）脚本不得注册对局级资产（效果 handler）——请改用 scope: match。");

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">全局相不支持对局级注册（配置错误）。</exception>
    public void DeclareBeforeWrapper(WrapperTarget target, string name, Action<object[]?> wrapper)
        => throw new InvalidOperationException(
            "全局（进程级）脚本不得声明对局级包装器——请改用 scope: match。");

    /// <inheritdoc />
    public void DeclareAfterWrapper(WrapperTarget target, string name, Func<object[]?, object[]?, object[]?> wrapper)
        => throw new InvalidOperationException(
            "全局（进程级）脚本不得声明对局级包装器——请改用 scope: match。");

    /// <inheritdoc />
    public void DeclareReplaceWrapper(WrapperTarget target, string name, Func<object[]?, object[]?> wrapper)
        => throw new InvalidOperationException(
            "全局（进程级）脚本不得声明对局级包装器——请改用 scope: match。");

    /// <inheritdoc />
    public void DeclareDependency(string assetName) => ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

    /// <inheritdoc />
    /// <exception cref="ArgumentException">assetName 为 null/空白。</exception>
    /// <exception cref="KeyNotFoundException">本包未声明该资产。</exception>
    public string ReadAsset(string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        foreach (var asset in _package.Assets)
        {
            if (string.Equals(asset.Asset.Name, assetName, StringComparison.Ordinal))
            {
                return File.ReadAllText(asset.AbsolutePath);
            }
        }

        throw new KeyNotFoundException(
            $"包 '{_package.Manifest.Id}' 未声明资产 '{assetName}'（读取失败——fail-fast）。");
    }
}
