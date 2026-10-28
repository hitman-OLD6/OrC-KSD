using Orc.Cards;
using Orc.Core;
using Orc.Game.Plugins;

namespace Orc.Packages;

/// <summary>
/// 注册目标面（受控注册集合）：资产服务注册表 ＋ 判定器注册表 ＋ 效果预制体管理器 ＋（可选）对局服务面。
/// 对局装配时由宿主构造（<c>Match</c> 侧句柄）；测试可注入独立实例。
/// </summary>
/// <param name="AssetServices">资产服务注册表。</param>
/// <param name="Judicators">判定器注册表。</param>
/// <param name="Prefabs">效果预制体管理器（引擎级）。</param>
/// <param name="Match">对局只读服务面（P10 甲；测试/非对局路径＝null）。</param>
/// <param name="Binding">对局绑定面（P11 甲·postCommand 相；非该相＝null）。</param>
public sealed record PluginRegistrationTargets(
    AssetServiceRegistry AssetServices,
    JudicatorRegistry Judicators,
    SnapshotStore Prefabs,
    IMatchServices? Match = null,
    IMatchBindingServices? Binding = null,
    Orc.Game.Ui.IUiPluginServices? Ui = null);

/// <summary>
/// 插件注册上下文（S8）：<see cref="IPluginRegistrationContext"/> 的实现——把 csx 注册入口的调用
/// **转发**到既有注册面（资产服务／判定器／效果 handler），不造第二注册面；
/// 并承接包身份、条目级依赖声明与本包数据读取。同时实现游戏层 <see cref="IMatchPluginContext"/>
/// （P10 甲：暴露对局服务面，供需协作者的判定器构造）。
/// </summary>
public sealed class PluginRegistrationContext : IPluginRegistrationContext, IMatchPluginContext
{
    private readonly LoadedPackage _package;
    private readonly PluginRegistrationTargets _targets;
    private readonly List<string> _dependencies = new();
    private readonly List<(WrapperTarget Target, string Name, WrapperPhase Phase, Delegate Wrapper)> _wrappers = new();

    /// <summary>创建注册上下文。</summary>
    /// <exception cref="ArgumentNullException">package 或 targets 为 null。</exception>
    public PluginRegistrationContext(LoadedPackage package, PluginRegistrationTargets targets)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(targets);
        _package = package;
        _targets = targets;
    }

    /// <inheritdoc />
    public string PackageId => _package.Manifest.Id;

    /// <inheritdoc />
    public string PackageGuid8 => _package.Manifest.Guid8;

    /// <inheritdoc />
    public IMatchServices? Match => _targets.Match;

    /// <inheritdoc />
    public IMatchBindingServices? Binding => _targets.Binding;

    /// <inheritdoc />
    public Orc.Game.Ui.IUiPluginServices? Ui => _targets.Ui;

    /// <summary>本次注册期间声明的条目级依赖（资产名；诊断/预检消费）。</summary>
    public IReadOnlyList<string> DeclaredDependencies => _dependencies;

    /// <inheritdoc />
    public void RegisterAssetService(string name, AssetService service)
        => _targets.AssetServices.Register(name, service);

    /// <inheritdoc />
    public JudicatorRegistration RegisterJudicator(string name, Judicator judicator)
        => _targets.Judicators.Register(name, judicator);

    /// <inheritdoc />
    public void RegisterHandler(string key, Delegate handler)
        => _targets.Prefabs.RegisterHandler(key, handler);

    /// <inheritdoc />
    /// <exception cref="ArgumentException">assetName 为 null/空白。</exception>
    public void DeclareDependency(string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        _dependencies.Add(assetName);
    }

    /// <inheritdoc />
    public void DeclareBeforeWrapper(WrapperTarget target, string name, Action<object[]?> wrapper)
    {
        ValidateDeclaration(target, name, wrapper);
        _wrappers.Add((target, name, WrapperPhase.Before, wrapper));
    }

    /// <inheritdoc />
    public void DeclareAfterWrapper(WrapperTarget target, string name, Func<object[]?, object[]?, object[]?> wrapper)
    {
        ValidateDeclaration(target, name, wrapper);
        _wrappers.Add((target, name, WrapperPhase.After, wrapper));
    }

    /// <inheritdoc />
    public void DeclareReplaceWrapper(WrapperTarget target, string name, Func<object[]?, object[]?> wrapper)
    {
        ValidateDeclaration(target, name, wrapper);
        _wrappers.Add((target, name, WrapperPhase.Replace, wrapper));
    }

    private static void ValidateDeclaration(WrapperTarget target, string name, Delegate wrapper)
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "未定义的包装器目标类别。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(wrapper);
    }

    /// <summary>本包声明的包装器（声明序；由宿主在注册完成后应用）。</summary>
    public IReadOnlyList<(WrapperTarget Target, string Name, WrapperPhase Phase, Delegate Wrapper)> DeclaredWrappers
        => _wrappers;

    public void ApplyWrappers()
    {
        foreach (var (target, name, phase, wrapper) in _wrappers)
        {
            switch (target)
            {
                case WrapperTarget.Judicator:
                    ApplyWrapper(_targets.Judicators, _targets.Judicators.Resolve(name), phase, wrapper);
                    break;
                case WrapperTarget.AssetService:
                    ApplyWrapper(_targets.AssetServices, _targets.AssetServices.Resolve(name), phase, wrapper);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "未定义的包装器目标类别。");
            }
        }
    }

    private static void ApplyWrapper<TService, THandle, TWrapperHandle>(
        ServiceRegistry<TService, THandle, TWrapperHandle> registry,
        THandle handle,
        WrapperPhase phase,
        Delegate wrapper)
        where TService : class, IUnifiedService
        where THandle : ServiceRegistration<TService>
        where TWrapperHandle : ServiceWrapperRegistration<TService>
    {
        switch (phase)
        {
            case WrapperPhase.Before:
                registry.RegisterBeforeWrapper(handle, (Action<object[]?>)wrapper);
                break;
            case WrapperPhase.After:
                registry.RegisterAfterWrapper(handle, (Func<object[]?, object[]?, object[]?>)wrapper);
                break;
            case WrapperPhase.Replace:
                registry.RegisterReplaceWrapper(handle, (Func<object[]?, object[]?>)wrapper);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "未定义的包装器类别。");
        }
    }

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
