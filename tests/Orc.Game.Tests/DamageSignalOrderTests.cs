using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Damage;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P1 阶段门（S1 硬约束二 / S5a 第 5 条）：伤害三入口（<c>CombatAsync</c>/<c>EffectAsync</c>/<c>BattleAsync</c>）
/// 的信号序列与顺序，在「伤害动作」迁移前后逐条一致。
/// <para>
/// 断言口径＝信号名＋载荷键序（载荷键序冻结，见 Constraints 1）；覆盖常规 / 重甲改写 / 免疫归零 / HQ / 致死五类场景。
/// </para>
/// </summary>
public class DamageSignalOrderTests
{
    private const string ArmorTwoId = "u_p1_armor2";
    private const string ImmuneId = "u_p1_immune";

    private static readonly IReadOnlyList<CardDefinitionEntry> ExtraDefinitions = new[]
    {
        new CardDefinitionEntry(ArmorTwoId, new CardDefinition("重甲2", 1, 1, 2, 5,
            unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Armor, 2) },
            faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(ImmuneId, new CardDefinition("免疫兵", 1, 1, 2, 5,
            unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Immune) },
            faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    [Fact]
    public async Task Combat_Normal_Signals()
    {
        var match = await CreateAsync();
        var target = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.BeastId, 0);
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.WeakId, 1);

        var signals = await CaptureAsync(match, () => match.CommandManager.DamagePipeline.CombatAsync(dealer, target, 3));

        Assert.Equal(new[]
        {
            "card.stat.changed{Card,ChangedFields,Source}",
            "card.damaged{Card,Amount,Source}",
            "unit.damage.dealt{Unit,Card,Amount,Source}",
        }, signals);
    }

    [Fact]
    public async Task Combat_Armor_Signals()
    {
        var match = await CreateAsync();
        var target = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], ArmorTwoId, 0);
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.WeakId, 1);

        var signals = await CaptureAsync(match, () => match.CommandManager.DamagePipeline.CombatAsync(dealer, target, 3));

        Assert.Equal(new[]
        {
            "card.stat.changed{Card,ChangedFields,Source}",
            "card.damaged{Card,Amount,Source}",
            "unit.damage.dealt{Unit,Card,Amount,Source}",
        }, signals);
        Assert.Equal(4, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 - (3 - 2)
    }

    [Fact]
    public async Task Effect_Immune_Zeroed_Signals()
    {
        var match = await CreateAsync();
        var target = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], ImmuneId, 0);
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.InfantryId, 1);

        var signals = await CaptureAsync(match, () => match.CommandManager.DamagePipeline.EffectAsync(dealer, target, 3));

        Assert.Empty(signals); // 归零＝不进门户、零发射
    }

    [Fact]
    public async Task Battle_Both_Sides_Signals()
    {
        var match = await CreateAsync();
        var first = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);
        var second = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.BeastId, 1);

        var signals = await CaptureAsync(match, () => match.CommandManager.DamagePipeline.BattleAsync(first, second, 2));

        Assert.Equal(new[]
        {
            "card.stat.changed{Card,ChangedFields,Source}",
            "card.damaged{Card,Amount,Source}",
            "unit.damage.dealt{Unit,Card,Amount,Source}",
        }, signals);
    }

    [Fact]
    public async Task Combat_Hq_Signals()
    {
        var match = await CreateAsync();
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.WeakId, 0);
        var hq = match.Players[1].Hq;

        var signals = await CaptureAsync(match, () => match.CommandManager.DamagePipeline.CombatAsync(dealer, hq, 5));

        Assert.Equal(new[]
        {
            "card.stat.changed{Card,ChangedFields,Source}",
            "card.damaged{Card,Amount,Source}",
            "unit.damage.dealt{Unit,Card,Amount,Source}",
        }, signals);
    }

    [Fact]
    public async Task Combat_Lethal_Signals()
    {
        var match = await CreateAsync();
        var target = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.WeakId, 0);
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.BeastId, 1);

        var signals = await CaptureAsync(match, () => match.CommandManager.DamagePipeline.CombatAsync(dealer, target, 99));

        Assert.Equal(new[]
        {
            "card.stat.changed{Card,ChangedFields,Source}",
            "card.dying{Card,Killer,Source}",
            "card.died{Card,Killer,Source}",
            "card.damaged{Card,Amount,Source}",
            "unit.damage.dealt{Unit,Card,Amount,Source}",
        }, signals);
        Assert.True(target.GetData<UnitStateData>().IsDestroyed);
    }

    [Fact]
    public async Task Effect_Damage_Runtime_Entry_Signals()
    {
        var match = await CreateAsync();
        var target = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.BeastId, 0);
        var dealer = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.WeakId, 1);

        var runtime = EffectRuntime.ResolveFor(target)!;
        var signals = await CaptureAsync(match, () => runtime.DamageAsync(target, 3, dealer));

        Assert.Equal(new[]
        {
            "card.stat.changed{Card,ChangedFields,Source}",
            "card.damaged{Card,Amount,Source}",
            "unit.damage.dealt{Unit,Card,Amount,Source}",
        }, signals);
    }

    private static async Task<Match> CreateAsync()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        return match;
    }

    private static async Task<List<string>> CaptureAsync(Match match, Func<Task> run)
    {
        var signals = new List<string>();
        using var subscription = match.Engine.Subscribe((type, payload, _) =>
        {
            signals.Add(payload is null || payload.Count == 0
                ? type
                : $"{type}{{{string.Join(",", payload.Keys)}}}");
            return Task.CompletedTask;
        });

        await run();
        return signals;
    }
}
