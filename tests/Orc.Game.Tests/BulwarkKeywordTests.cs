using Orc.Cards;
using Orc.Game.Cards;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>铜墙铁壁系列（机动/固守/抢修）行为验收：csx 制品装载 + 额度/判定器/回合统计接线。</summary>
public sealed class BulwarkKeywordTests : IDisposable
{
    /// <summary>清理进程级词条注册（避免全局绑定核验影响其他测试）。</summary>
    public void Dispose()
    {
        KeywordRegistry.Unregister(Mobility);
        KeywordRegistry.Unregister(Entrench);
        KeywordRegistry.Unregister(Repair);
    }

    private const string Mobility = "机动";
    private const string Entrench = "固守";
    private const string Repair = "抢修";

    private static readonly string PackRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../samples/card-pack-bulwark"));

    private static readonly object Gate = new();

    private static string PrefabId(string keyword) => keyword switch
    {
        Mobility => "keyword.mobility",
        Entrench => "keyword.entrench",
        _ => "keyword.repair",
    };

    private static string EffectName(string keyword) => keyword switch
    {
        Mobility => "机动·免费行动",
        Entrench => "固守·不可移动",
        _ => "抢修·回合修复",
    };

    private static void EnsureKeyword(string keyword)
    {
        lock (Gate)
        {
            if (!KeywordRegistry.IsDefined(keyword))
            {
                KeywordRegistry.Register(keyword, (_, _) => new PlainKeywordComponent(keyword));
            }

            KeywordRegistry.DeclareEffectBindings(
                keyword, [new KeywordEffectBinding(PrefabId(keyword), EffectName(keyword))]);
        }
    }

    private static (Match Match, MockTargeterBridge Bridge) CreateMatch(
        string cardId, string keyword, params string[] extraKeywords)
    {
        EnsureKeyword(keyword);
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(
            bridge,
            extraDefinitions:
            [
                new CardDefinitionEntry(cardId, new CardDefinition(
                    "测试单位", 1, 0, 2, 5,
                    unitTypes: [UnitType.Infantry],
                    keywords: [new KeywordDeclaration(keyword), .. extraKeywords.Select(k => new KeywordDeclaration(k))],
                    faction: Faction.Germany,
                    rarity: Rarity.Standard)),
            ]);

        match.Engine.ScriptEvaluator = new CSharpScriptEvaluator();
        match.Engine.SyncScriptEvaluator = new CSharpSyncScriptEvaluator();
        foreach (var id in new[] { "keyword.mobility", "keyword.entrench", "keyword.repair" })
        {
            match.Engine.Prefabs.RegisterPrefab(PrefabJson.Deserialize(File.ReadAllText(
                Path.Combine(PackRoot, "effects", id + ".prefab.json"))));
        }

        return (match, bridge);
    }

    [Fact]
    public async Task 机动_首次攻击免额度消耗_并失去词条()
    {
        var (match, bridge) = CreateMatch("test.mobility", Mobility);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.mobility", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var quotaBefore = unit.GetData<CommandData>().AttackQuota;
        Assert.True(unit.Keywords.Has(Mobility));
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, match.Players[1].Hq.Ref)).IsSuccess);

        Assert.False(unit.Keywords.Has(Mobility));
        Assert.Equal(quotaBefore, unit.GetData<CommandData>().AttackQuota); // 本次攻击未扣（免费）
    }

    [Fact]
    public async Task 机动_奋战_先攻击可打三次且不能移动()
    {
        var (match, bridge) = CreateMatch("test.mobility.fury", Mobility, KeywordIds.Fury);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.mobility.fury", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        Assert.Equal(2, unit.GetData<CommandData>().AttackQuota); // 奋战：基础 +1

        var hq = match.Players[1].Hq.Ref;
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, hq)).IsSuccess);
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, hq)).IsSuccess);
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, hq)).IsSuccess);
        Assert.False(unit.Keywords.Has(Mobility));
        Assert.False(match.CommandManager.GetCommandAvailability(unit).Move.CanUse, "机动+奋战先攻击：不能移动");
        Assert.False((await match.CommandManager.BeginAttackAsync(unit)).IsSuccess);
    }

    [Fact]
    public async Task 固守_不能指挥移动_可被效果位移_不可被抑制()
    {
        var (match, _) = CreateMatch("test.entrench", Entrench);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.entrench", match.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(unit);

        Assert.False(match.CommandManager.GetCommandAvailability(unit).Move.CanUse, "固守：不能指挥移动");

        var support = match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0);
        Assert.True(await match.CommandManager.MoveUnitAsync(unit, support)); // 效果位移允许

        Assert.NotNull(match.Engine.Judicators);
        Assert.True(match.Engine.Judicators!.Contains("inhibit.leg.eligibility"), "抑制判定器应已注册");

        // 诊断：固守的抑制包装器 csx 应可求值。
        var snapshot = PrefabJson.Deserialize(File.ReadAllText(
            Path.Combine(PackRoot, "effects", "keyword.entrench.prefab.json")));
        var immune = snapshot.Root.JudicatorWrappers.Single(j => j.JudicatorName == "inhibit.leg.eligibility");
        var evaluated = match.Engine.SyncScriptEvaluator!.Evaluate(
            new Orc.Core.SyncScriptRequest(immune.Handler.CsxSource!, immune.Handler.EntryName));
        Assert.True(evaluated.Success, evaluated.Error);

        var raw = match.Engine.Judicators.Invoke("inhibit.leg.eligibility", new object?[] { unit });
        Assert.True(
            raw is { Length: > 0 } && raw[0] is Orc.Game.Judicators.InhibitEligibilityFailure,
            $"包装器直调结果：{raw?[0]?.ToString() ?? "null"}");

        Assert.False(await InhibitRules.ApplyAsync(unit), "固守：不可被抑制");
        Assert.True(unit.Keywords.Has(Entrench));
    }

    [Fact]
    public async Task 抢修_移动到前线即失去词条()
    {
        var (match, bridge) = CreateMatch("test.repair.move", Repair);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.repair.move", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        CommandTestKit.Activate(unit);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);

        var front = match.Battlefield.FrontLine.GetInsertionGaps().At(0);
        Assert.True((await CommandTestKit.RunCommandAsync(match, bridge, unit, front.Ref)).IsSuccess);
        Assert.Same(front, unit.GetData<UnitStateData>().Line);
        Assert.False(unit.Keywords.Has(Repair), "抢修：位于前线即失去");
    }

    [Fact]
    public async Task 抢修_己方回合开始_未移动未攻击则回满()
    {
        var (match, _) = CreateMatch("test.repair", Repair);
        await match.Initialize();
        var unit = await CommandTestKit.PrepareUnitAsync(
            match, match.Players[0], "test.repair", match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));

        await unit.ApplyDefenseDamageAsync(2, CancellationToken.None);
        Assert.True(unit.GetData<UnitStateData>().DefenseLoss > 0);

        await match.Engine.Emit(
            GameUpdates.TurnStart,
            new Dictionary<string, object?> { [GameUpdates.PayloadPlayer] = match.Players[0] });

        Assert.Equal(0, unit.GetData<UnitStateData>().DefenseLoss);
    }
}
