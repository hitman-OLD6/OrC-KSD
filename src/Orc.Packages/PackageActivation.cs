using Orc.Cards;
using Orc.Core;
using Orc.Game.Plugins;

namespace Orc.Packages;

/// <summary>被跳过的包（S14 第 2–3 条）：包 id ＋ 必需/可选 ＋ 失败项。</summary>
/// <param name="PackageLabel">包标签（<c>id@version</c>）。</param>
/// <param name="Required">是否必需包。</param>
/// <param name="Failures">失败项。</param>
public sealed record PackageLoadSkip(string PackageLabel, bool Required, IReadOnlyList<string> Failures);

/// <summary>装配报告：已激活包（装配序）／条目级依赖声明／失败明细／被跳过的包（S14）。</summary>
/// <param name="ActivatedPackages">已激活包（<c>id@version</c>；装配序）。</param>
/// <param name="DeclaredDependencies">条目级依赖声明（声明序）。</param>
/// <param name="Failures">失败明细（全部，含必需包）。</param>
/// <param name="Skipped">被跳过的**可选**包（必需包失败另经 <see cref="RequiredFailures"/> 表达）。</param>
public sealed record PackageActivationReport(
    IReadOnlyList<string> ActivatedPackages,
    IReadOnlyList<string> DeclaredDependencies,
    IReadOnlyList<string> Failures,
    IReadOnlyList<PackageLoadSkip> Skipped)
{
    /// <summary>失败的必需包（非空＝对局不开局，S14 第 2 条）。</summary>
    public IReadOnlyList<PackageLoadSkip> RequiredFailures
        => Skipped.Where(static s => s.Required).ToArray();

    /// <summary>被跳过的可选包。</summary>
    public IReadOnlyList<PackageLoadSkip> SkippedOptional
        => Skipped.Where(static s => !s.Required).ToArray();
}

/// <summary>
/// 对局级包激活（S10／P11 甲）：把快照中**属于给定执行相**的对局级脚本按
/// **装配序（插件先、卡包后）**逐个执行——注册判定器／资产服务／效果 handler／流程触发器登记等，
/// 随后**应用该包声明的 moding**（声明序、栈语义后声明者胜）。
/// 作用域过滤：仅 <see cref="AssetScope.Match"/> 脚本参与；<see cref="AssetScope.Process"/> 脚本由进程级装配承担。
/// 失败语义分相：<see cref="AssetActivationPhase.PreCommand"/>＝**注册**（单包失败＝隔离该包、不阻断其余）；
/// <see cref="AssetActivationPhase.PostCommand"/>＝**装配不变量**（异常上抛＝建局 fail-fast，如词条效果库绑定核验失败）。
/// moding **只作用于本次传入的注册表**（对局级）⇒ 不跨对局泄漏。
/// </summary>
public static class PackageActivation
{
    /// <summary>激活一个快照的某一执行相（对局装配期按相各调用一次）。</summary>
    /// <param name="snapshot">资产目录快照。</param>
    /// <param name="assetServices">资产服务注册表。</param>
    /// <param name="judicators">判定器注册表。</param>
    /// <param name="prefabs">效果预制体管理器。</param>
    /// <param name="matchServices">对局服务面（P10 甲；测试/非对局路径＝null）。</param>
    /// <param name="bindingServices">对局绑定面（P11 甲·postCommand 相；其余相＝null）。</param>
    /// <param name="phase">本次执行的相。</param>
    /// <exception cref="ArgumentNullException">snapshot／assetServices／judicators／prefabs 为 null。</exception>
    public static PackageActivationReport Activate(
        AssetCatalogSnapshot snapshot,
        AssetServiceRegistry assetServices,
        JudicatorRegistry judicators,
        SnapshotStore prefabs,
        IMatchServices? matchServices = null,
        IMatchBindingServices? bindingServices = null,
        AssetActivationPhase phase = AssetActivationPhase.PreCommand,
        Orc.Game.Ui.IUiPluginServices? uiServices = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(assetServices);
        ArgumentNullException.ThrowIfNull(judicators);
        ArgumentNullException.ThrowIfNull(prefabs);

        var targets = new PluginRegistrationTargets(
            assetServices, judicators, prefabs, matchServices, bindingServices, uiServices);
        var activated = new List<string>();
        var dependencies = new List<string>();
        var failures = new List<string>();
        var skipped = new List<PackageLoadSkip>();

        foreach (var package in snapshot.Packages)
        {
            var scripts = package.Scripts
                .Where(s => s.Scope == AssetScope.Match && s.Phase == phase)
                .ToArray();
            if (scripts.Length == 0)
            {
                continue;
            }

            var label = $"{package.Manifest.Id}@{package.Manifest.Version}";
            var context = new PluginRegistrationContext(package, targets);

            if (phase == AssetActivationPhase.PostCommand)
            {
                // 装配不变量相：异常上抛（建局 fail-fast）——不隔离、不吞。
                foreach (var script in scripts)
                {
                    script.Register(context);
                }

                context.ApplyWrappers();
                activated.Add(label);
                dependencies.AddRange(context.DeclaredDependencies);
                continue;
            }

            try
            {
                foreach (var script in scripts)
                {
                    script.Register(context);
                }

                context.ApplyWrappers();

                activated.Add(label);
                dependencies.AddRange(context.DeclaredDependencies);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{label}：{ex.Message}");

                // S14 第 2 条：必需包失败＝对局不开局（由装配段按 RequiredFailures 上抛）；
                // 可选包失败＝跳过该包并留痕（报告＋审查面）。这里只分类，不吞、不抛。
                skipped.Add(new PackageLoadSkip(label, package.Manifest.Required, new[] { ex.Message }));
            }
        }

        return new PackageActivationReport(activated, dependencies, failures, skipped);
    }
}
