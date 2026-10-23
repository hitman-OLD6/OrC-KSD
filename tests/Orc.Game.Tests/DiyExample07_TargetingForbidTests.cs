using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Effects;
using Orc.Game.Judicators;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 7（R6g）证据：**使一个单位无法被指令指向**——两条路径各一条用例。
/// ① 代码侧：<see cref="TargetingDeclaration"/> 包装「目标候选名单判定器」（After：从名单剔除被保护目标）。
/// ② 混合：词条程序 <c>targeting.forbid</c> ＋ <c>ProgramKeywordComponent.Register</c>（规则 JSON，注册需一行 C#）。
/// 断言：候选名单中被保护单位消失、旁证单位仍在。
/// </summary>
public class DiyExample07_TargetingForbidTests
{
    private const string KeywordId = "diy.example7.forbid";

    [Fact]
    public async Task 代码侧声明_被保护单位从敌方指令候选中剔除()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var guarded = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var bystander = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var viewer = await PrepareCommandViewerAsync(match, playerB);

        var declaration = new TargetingDeclaration(
            match.Judicators,
            guarded,
            candidate => ReferenceEquals(candidate, guarded),
            () => true,
            sourceKind: "command");
        declaration.Attach(viewer.Owner!.Index);
        try
        {
            var candidates = Candidates(match, viewer);

            Assert.DoesNotContain(candidates, reference => ReferenceEquals(reference.Value, guarded));
            Assert.Contains(candidates, reference => ReferenceEquals(reference.Value, bystander));
        }
        finally
        {
            declaration.Detach();
        }
    }

    [Fact]
    public async Task 词条程序路径_被保护单位从敌方指令候选中剔除()
    {
        // 注册词条程序（一次性，必须早于建局——对局在 Initialize 时冻结词条快照）。
        ProgramKeywordComponent.Register(
            KeywordId,
            KeywordProgram.Parse(
                "{\"schema\":1,\"rules\":[{\"rule\":\"targeting.forbid\",\"scope\":\"self\",\"value\":\"1\",\"source\":\"command\"}]}"));

        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var guarded = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var bystander = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var viewer = await PrepareCommandViewerAsync(match, playerB);

        Assert.True(await guarded.Keywords.GrantAsync(KeywordId));

        var candidates = Candidates(match, viewer);

        Assert.DoesNotContain(candidates, reference => ReferenceEquals(reference.Value, guarded));
        Assert.Contains(candidates, reference => ReferenceEquals(reference.Value, bystander));
    }

    /// <summary>
    /// 回归（b5e0a5c）：真实产品路径——卡组内含 <c>targeting.forbid</c> 程序词条时，
    /// 词条装载（<c>Mount</c> → 声明挂载）发生在 <see cref="Orc.Game.Match.Initialize"/> 的准备态；
    /// 声明挂载改走装配链内部访问面后，不得再被公共 Players/Judicators 的准备态门禁拦死。
    /// 断言：① 初始化成功进入"进行"态；② 该单位入场的声明仍生效（候选被剔除——非"跳过挂载"式假修复）。
    /// </summary>
    [Fact]
    public async Task 卡组含targeting_forbid程序词条_Initialize装配期挂载声明不被准备态门禁拦死()
    {
        const string keywordId = "diy.example7.deck.forbid";
        const string unitId = "u_forbid_deck";

        ProgramKeywordComponent.Register(
            keywordId,
            KeywordProgram.Parse(
                "{\"schema\":1,\"rules\":[{\"rule\":\"targeting.forbid\",\"scope\":\"self\",\"value\":\"1\",\"source\":\"command\"}]}"));

        var definitions = CommandTestKit.CreateDefinitions().Concat(new[]
        {
            new CardDefinitionEntry(
                unitId,
                new CardDefinition(
                    "禁指步兵", 1, 1, 2, 4,
                    unitTypes: new[] { UnitType.Infantry },
                    keywords: new[] { new KeywordDeclaration(keywordId) },
                    faction: Faction.Germany,
                    rarity: Rarity.Standard)),
        });

        var match = new Orc.Game.Match(
            new CardList(Enumerable.Repeat(unitId, 10)),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            definitions,
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            packageActivator: TestHost.Activator);

        // 卡加载＝准备态：声明挂载即发生在此（回归点）。
        await match.Initialize();

        Assert.Equal(MatchState.InProgress, match.State);

        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var guarded = playerA.Hand.OfType<UnitCard>()
            .First(card => match.CardService.TryResolveDefinitionId(card, out var id) && id == unitId);
        var slot = match.Battlefield.GetSupportLine(playerA).GetInsertionGaps().At(0);
        var joined = await match.PlayManager.JoinUnitAsync(guarded, slot);
        Assert.Equal(PlayResultStatus.Success, joined.Status);

        var viewer = await PrepareCommandViewerAsync(match, playerB);
        var candidates = Candidates(match, viewer);
        Assert.DoesNotContain(candidates, reference => ReferenceEquals(reference.Value, guarded));
    }

    /// <summary>以「敌方指令」为视角取候选名单（玩家域＝viewer 的归属域）。</summary>
    private static IReadOnlyList<Orc.Core.Ref<Orc.Core.Entity>> Candidates(
        Orc.Game.Match match, OrderCard viewer)
    {
        var rule = new JudicatorSelectionRule(
            match.Judicators.Resolve(JudicatorNames.TargetCandidateEligibility),
            viewer,
            new EffectSelector("one", "enemy"));
        return rule.Candidates();
    }

    /// <summary>准备指令卡作为「敌方指令」视角（在手即可；候选按归属域计算）。</summary>
    private static async Task<OrderCard> PrepareCommandViewerAsync(
        Orc.Game.Match match, Orc.Game.Players.Player player)
    {
        var viewer = (OrderCard)match.CardLibrary.Instantiate(CommandTestKit.OrderCardId);
        await viewer.LoadAsync(player);
        player.Hand.Add(viewer);
        return viewer;
    }
}
