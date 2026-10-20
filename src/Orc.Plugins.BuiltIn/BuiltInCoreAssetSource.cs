using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Cards.Data.Components;
using Orc.Game.Judicators;
using Orc.Packages;

namespace Orc.Plugins.BuiltIn;

/// <summary>
/// <c>orc.builtin.core</c> 的服务资产声明（D63/Q34 试点）：判定器 24 ＋ 词条 19 ＋ 卡组件 6 ＝ 49 条。
/// <para>
/// 代码声明是权威；<c>assets.meta.json</c> 与两 lock 由 <see cref="PluginAssetManifestWriter"/> 生成。
/// 注册名一律引用受控常量（`JudicatorNames`／`KeywordIds`／各 `DefinitionName`），不写字面量。
/// </para>
/// </summary>
public sealed class BuiltInCoreAssetSource : IPluginAssetSource
{
    /// <summary>声明方程序集名（写入清单 <c>source</c> 字段——asm 插件的"源"）。</summary>
    public const string AssemblyName = "Orc.Plugins.BuiltIn";

    /// <summary>全部服务资产的初始版本（官方更新插件时递增）。</summary>
    public static readonly AssetVersion InitialVersion = AssetVersion.Parse("1.0.0");

    /// <summary>已注册的判定器（24 条；不含 `action.input.allowed`／`validation.inhibit.eligibility`／`deck.top.tag` 等非本包注册项）。</summary>
    public static IReadOnlyList<string> JudicatorNamesInPlugin { get; } =
    [
        JudicatorNames.ActionTurnOwner,
        JudicatorNames.CostCheck,
        JudicatorNames.CounterUse,
        JudicatorNames.CombatRange,
        JudicatorNames.CombatGuardEligibility,
        JudicatorNames.CombatInterception,
        JudicatorNames.CombatTargetLegal,
        JudicatorNames.CombatCounterEligibility,
        JudicatorNames.CombatAmbushCondition,
        JudicatorNames.MoveLegEligibility,
        JudicatorNames.AttackLegEligibility,
        JudicatorNames.MoveFrontlineEnemy,
        JudicatorNames.MoveRecheck,
        JudicatorNames.AttackRecheck,
        JudicatorNames.TargetCandidateEligibility,
        JudicatorNames.EffectTargetResolve,
        JudicatorNames.PointSlotIncrement,
        JudicatorNames.PointSlotGain,
        JudicatorNames.PointSlotLose,
        JudicatorNames.PointGain,
        JudicatorNames.PointLose,
        JudicatorNames.UnitAdjacent,
        JudicatorNames.UnitTypeMatch,
        JudicatorNames.UnitSide,
    ];

    /// <summary>已注册的卡数据组件（6 条；注册序＝两段式加载第一段）。</summary>
    public static IReadOnlyList<string> CardComponentNames { get; } =
    [
        TypeCategoryDefinition.DefinitionName,
        FactionCostDefinition.DefinitionName,
        BattleStatsDefinition.DefinitionName,
        TagDataDefinition.DefinitionName,
        KeywordsDefinition.DefinitionName,
        EffectsDefinition.DefinitionName,
    ];

    /// <inheritdoc />
    public string PackageId => BuiltInPluginPackage.PackageId;

    /// <inheritdoc />
    public IReadOnlyList<PluginAssetDescriptor> Describe()
    {
        var descriptors = new List<PluginAssetDescriptor>(
            JudicatorNamesInPlugin.Count + KeywordIds.All.Count + CardComponentNames.Count);

        foreach (var name in JudicatorNamesInPlugin)
        {
            descriptors.Add(new PluginAssetDescriptor(
                ServiceKindTokens.Judicator, name, InitialVersion, DependenciesOf(name)));
        }

        foreach (var keyword in KeywordIds.All)
        {
            descriptors.Add(new PluginAssetDescriptor(
                ServiceKindTokens.Keyword, keyword, InitialVersion));
        }

        foreach (var component in CardComponentNames)
        {
            descriptors.Add(new PluginAssetDescriptor(
                ServiceKindTokens.CardComponent, component, InitialVersion));
        }

        return descriptors;
    }

    /// <summary>判定器的既有真实依赖（构造期句柄注入；来源见 <c>BuiltInPlugin.cs</c>）。</summary>
    private static IReadOnlyList<AssetReference>? DependenciesOf(string judicatorName) => judicatorName switch
    {
        JudicatorNames.CounterUse =>
            [Judicator(JudicatorNames.ActionTurnOwner)],

        JudicatorNames.CombatTargetLegal =>
            [
                Judicator(JudicatorNames.CombatRange),
                Judicator(JudicatorNames.CombatGuardEligibility),
                Judicator(JudicatorNames.CombatInterception),
            ],

        JudicatorNames.MoveRecheck =>
            [
                Judicator(JudicatorNames.MoveLegEligibility),
                Judicator(JudicatorNames.MoveFrontlineEnemy),
            ],

        JudicatorNames.AttackRecheck =>
            [
                Judicator(JudicatorNames.AttackLegEligibility),
                Judicator(JudicatorNames.MoveFrontlineEnemy),
            ],

        JudicatorNames.EffectTargetResolve =>
            [Judicator(JudicatorNames.TargetCandidateEligibility)],

        _ => null,
    };

    private static AssetReference Judicator(string toName)
        => PluginAssetRegistrar.ServiceReference(
            BuiltInPluginPackage.PackageId, ServiceKindTokens.Judicator, toName);
}
