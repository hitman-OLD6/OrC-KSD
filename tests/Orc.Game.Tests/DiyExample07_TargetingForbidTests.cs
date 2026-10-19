using Orc.Game.Cards;
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
