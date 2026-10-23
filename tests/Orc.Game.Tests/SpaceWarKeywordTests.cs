using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>太空战争系列（强磁护盾 / 磁反应装甲 / 海绵装甲 / 游击）行为验收：伤害规则、指向扣层、撤退规则。</summary>
public sealed class SpaceWarKeywordTests : IDisposable
{
    private const string Shield = "强磁护盾";
    private const string Reaction = "磁反应装甲";
    private const string Sponge = "海绵装甲";
    private const string Guerrilla = "游击";

    private static readonly string PackRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../samples/card-pack-space-war"));

    private static readonly object Gate = new();

    public void Dispose()
    {
        KeywordRegistry.Unregister(Shield);
        KeywordRegistry.Unregister(Reaction);
        KeywordRegistry.Unregister(Sponge);
        KeywordRegistry.Unregister(Guerrilla);
    }

    private static string PrefabId(string keyword) => keyword switch
    {
        Shield => "keyword.magnetic-shield",
        Reaction => "keyword.magnetic-reaction",
        _ => "keyword.sponge-armor",
    };

    private static string EffectName(string keyword) => keyword switch
    {
        Shield => "强磁护盾·伤害减免",
        Reaction => "磁反应装甲·免疫",
        _ => "海绵装甲·伤害封顶",
    };

    private static void EnsureKeyword(string keyword)
    {
        lock (Gate)
        {
            if (keyword == Guerrilla)
            {
                if (!KeywordRegistry.IsDefined(Guerrilla))
                {
                    ProgramKeywordComponent.Register(
                        Guerrilla,
                        KeywordProgram.Parse(
                            "{\"schema\":1,\"rules\":[{\"scope\":\"self\",\"rule\":\"move.retreat\",\"value\":\"1\"}]}"));
                }

                return;
            }

            if (!KeywordRegistry.IsDefined(keyword))
            {
                KeywordRegistry.Register(
                    keyword,
                    (_, value) => new PlainKeywordComponent(keyword, value),
                    isBattleKeyword: false);
            }

            KeywordRegistry.DeclareEffectBindings(
                keyword, [new KeywordEffectBinding(PrefabId(keyword), EffectName(keyword))]);
        }
    }

    private static Match CreateMatch(
        string cardId,
        IReadOnlyList<KeywordDeclaration> keywords,
        MockTargeterBridge bridge)
    {
        foreach (var declaration in keywords)
        {
            EnsureKeyword(declaration.Id);
        }

        var match = CommandTestKit.CreateCommandMatch(
            bridge,
            extraDefinitions:
            [
                new CardDefinitionEntry(cardId, new CardDefinition(
                    "测试单位", 1, 0, 2, 5,
                    unitTypes: [UnitType.Infantry],
                    keywords: keywords,
                    faction: Faction.Germany,
                    rarity: Rarity.Standard)),
            ]);

        match.Engine.ScriptEvaluator = new CSharpScriptEvaluator();
        match.Engine.SyncScriptEvaluator = new CSharpSyncScriptEvaluator();
        foreach (var id in new[] { "keyword.magnetic-shield", "keyword.magnetic-reaction", "keyword.sponge-armor" })
        {
            match.Engine.Prefabs.RegisterPrefab(PrefabJson.Deserialize(File.ReadAllText(
                Path.Combine(PackRoot, "effects", id + ".prefab.json"))));
        }

        return match;
    }

    private static int Loss(UnitCard unit) => unit.GetData<UnitStateData>().DefenseLoss;

    [Fact]
    public async Task 强磁护盾_三类伤害各减一()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch("test.shield", [new KeywordDeclaration(Shield)], bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.shield", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));

        await match.EffectRuntime.DamageAsync(unit, 2);
        Assert.Equal(1, Loss(unit));

        await match.CommandManager.DamagePipeline.CombatAsync(null, unit, 2);
        Assert.Equal(2, Loss(unit));

        await match.CommandManager.DamagePipeline.BattleAsync(null, unit, 2);
        Assert.Equal(3, Loss(unit));
    }

    [Fact]
    public async Task 强磁护盾_一点伤害归零_且不发射受伤信号()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch("test.shield.zero", [new KeywordDeclaration(Shield)], bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.shield.zero", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));

        var damaged = 0;
        match.Engine.Subscribe((kind, _, _) =>
        {
            if (string.Equals(kind, GameUpdates.CardDamaged, StringComparison.Ordinal))
            {
                damaged++;
            }

            return Task.CompletedTask;
        });

        await match.EffectRuntime.DamageAsync(unit, 1);

        Assert.Equal(0, Loss(unit));
        Assert.Equal(0, damaged);
    }

    [Fact]
    public async Task 强磁护盾_指挥攻击后失去_本次仍减伤()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch("test.shield.attack", [new KeywordDeclaration(Shield)], bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.shield.attack", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        Assert.True(unit.Keywords.Has(Shield));

        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Players[1].Hq.Ref);

        Assert.True(result.IsSuccess);
        var errors = match.Engine.RootStream.Entries
            .Where(e => e.Level == LogLevel.Error)
            .Select(e => e.Source + "：" + e.Message)
            .ToArray();
        Assert.False(
            unit.Keywords.Has(Shield),
            "指挥攻击后应失去「强磁护盾」。错误日志：" + string.Join(" | ", errors));
    }

    [Fact]
    public async Task 磁反应装甲_伤害归零_被敌方指向扣层_归零移除()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch("test.reaction", [new KeywordDeclaration(Reaction, 2)], bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.reaction", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        var enemy = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[1], "test.reaction", match.Battlefield.PlayerBSupportLine.GetInsertionGaps().At(1));

        Assert.Equal(2, unit.Keywords.GetValue(Reaction));

        await match.EffectRuntime.DamageAsync(unit, 4);
        Assert.Equal(0, Loss(unit)); // 免疫：伤害归零

        // 友方指向不扣层。
        await TargetingRules.NotifyAsync(match.Engine, unit, match.Players[0].Hq, TargetingRules.KindEffect);
        Assert.Equal(2, unit.Keywords.GetValue(Reaction));

        // 敌方指向：2 → 1（仍免疫）。
        await TargetingRules.NotifyAsync(
            match.Engine, unit, match.Players[1].Hq, TargetingRules.KindEffect);
        Assert.Equal(1, unit.Keywords.GetValue(Reaction));

        await match.EffectRuntime.DamageAsync(unit, 4);
        Assert.Equal(0, Loss(unit));

        // 再次指向：归 0 → 失去词条，随后不再免疫。
        await TargetingRules.NotifyAsync(
            match.Engine, unit, match.Players[1].Hq, TargetingRules.KindCommand);
        Assert.False(unit.Keywords.Has(Reaction));

        await match.EffectRuntime.DamageAsync(unit, 4);
        Assert.Equal(4, Loss(unit));
    }

    [Fact]
    public async Task 海绵装甲_持强磁护盾时伤害至多一()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch(
            "test.sponge", [new KeywordDeclaration(Shield), new KeywordDeclaration(Sponge)], bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.sponge", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));

        await match.EffectRuntime.DamageAsync(unit, 5);
        Assert.Equal(1, Loss(unit));

        // 1 点伤害：强磁 -1 → 0（不受伤）。
        await match.EffectRuntime.DamageAsync(unit, 1);
        Assert.Equal(1, Loss(unit));
    }

    [Fact]
    public async Task 海绵装甲_无强磁护盾时不封顶()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch("test.sponge.alone", [new KeywordDeclaration(Sponge)], bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.sponge.alone", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));

        await match.EffectRuntime.DamageAsync(unit, 3);
        Assert.Equal(3, Loss(unit));
    }

    [Fact]
    public async Task 游击_前线可指挥移动回支援线_无游击不可()
    {
        var bridge = new MockTargeterBridge();
        var match = CreateMatch("test.guerrilla", [new KeywordDeclaration(Guerrilla)], bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.guerrilla", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        Assert.True(match.CommandManager.GetCommandAvailability(unit).Move.CanUse, "游击：前线可撤退");

        var support = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0);
        var result = await CommandTestKit.RunCommandAsync(match, bridge, unit, support.Ref);

        Assert.True(result.IsSuccess);
        Assert.Same(support, unit.GetData<UnitStateData>().Line);
    }

    [Fact]
    public async Task 游击_无词条单位在前线不可指挥移动()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], CommandTestKit.InfantryId, match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);

        Assert.False(match.CommandManager.GetCommandAvailability(unit).Move.CanUse);
    }
}
