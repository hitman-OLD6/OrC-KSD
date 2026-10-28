using Orc.Cards;
using Orc.Core;
using Orc.Game.Plugins;

namespace Orc.Packages;

/// <summary>
/// 快照包激活器（宿主层实现 <see cref="IMatchPackageActivator"/>；接口倒置——游戏层不认识本工程）：
/// 把一份资产目录快照的**指定执行相**按装配序激活到本次对局的注册面，
/// 并把对局服务面／对局绑定面透传给插件上下文（P10／P11 甲）。
/// </summary>
public sealed class SnapshotPackageActivator : IMatchPackageActivator
{
    private readonly AssetCatalogSnapshot _snapshot;

    /// <summary>创建激活器。</summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public SnapshotPackageActivator(AssetCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
    }

    /// <inheritdoc />
    public MatchPackageActivationResult Activate(
        AssetActivationPhase phase,
        IMatchServices? matchServices,
        IMatchBindingServices? bindingServices,
        AssetServiceRegistry assetServices,
        JudicatorRegistry judicators,
        SnapshotStore prefabs,
        Orc.Game.Ui.IUiPluginServices? uiServices = null)
    {
        var report = PackageActivation.Activate(
            _snapshot, assetServices, judicators, prefabs, matchServices, bindingServices, phase, uiServices);
        return new MatchPackageActivationResult(
            report.ActivatedPackages,
            report.Failures,
            report.Skipped
                .Select(static skip => new Orc.Game.Plugins.MatchPackageSkip(skip.PackageLabel, skip.Required, skip.Failures))
                .ToArray());
    }
}
