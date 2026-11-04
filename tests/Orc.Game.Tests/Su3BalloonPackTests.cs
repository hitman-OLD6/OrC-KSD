using Orc.Cards;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Effects;
using Orc.Game.Managers;
using Orc.Packages;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R5（按条件检索）／R6（动态效果授予）／R9（卡包落地）** 验收：
/// 两个新包可装载、预检零错、元数据由迁移工具产出；检索三态与「无匹配＝正常空转」；
/// 授予四方法的成功/失败/幂等形态。
/// </summary>
public class Su3BalloonPackTests
{
    // ==================== R9：卡包落地 ====================

    [Fact]
    public void 两个卡包预检零错_元数据由内容指纹产出_二次迁移零待写()
    {
        if (!Su3BalloonKit.TryLocate(out var su3Cards, out var balloonCards, out var su3Effects, out var balloonEffects))
        {
            return;
        }

        var report = CardDistributionValidator.Validate(
            new[] { su3Cards, balloonCards }, new[] { su3Effects, balloonEffects });

        Assert.Empty(report.Errors);
        Assert.True(report.HasNoErrors);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(2, report.SuccessfulCardCount);

        var root = Su3BalloonKit.FindRepoRoot();
        foreach (var (id, cardPrefix) in new[] { ("su3", "su3."), ("balloon", "balloon.") })
        {
            var packRoot = Path.Combine(root, "packs", id);
            var manifest = File.ReadAllText(Path.Combine(packRoot, "manifest.json"));

            Assert.Contains($"\"id\": \"{id}\"", manifest, StringComparison.Ordinal);
            Assert.Contains("\"kind\": \"cardpack\"", manifest, StringComparison.Ordinal);
            Assert.True(Guid.TryParse(ExtractValue(manifest, "guid"), out _), "包 guid 必须由工具产出且合法。");
            Assert.Matches("\"version\": \"[0-9a-f]{32}\"", manifest); // 内容指纹（非手填版本号）
            Assert.StartsWith(cardPrefix, id + ".", StringComparison.Ordinal);

            // Apply 二次运行＝零待写文件（幂等，指纹不漂）。
            var plan = AssetMigrationTool.Plan(packRoot);
            Assert.Empty(plan.Obstacles);
            Assert.Empty(plan.PendingWrites());
        }

        // 压制效果预制体是 balloon 包内资产（R9 明文）。
        Assert.True(File.Exists(Path.Combine(balloonEffects, Su3BalloonKit.PinPrefabId + ".component.json")));
    }

    // ==================== R5：按条件检索 ====================

    [Fact]
    public async Task 兵种数组检索_命中战机并走既有抽牌链路()
    {
        if (!Su3BalloonKit.TryLocate(out var su3Cards, out var balloonCards, out var su3Effects, out var balloonEffects))
        {
            return;
        }

        var pack = Su3BalloonKit.LoadPack(su3Cards, balloonCards, su3Effects, balloonEffects);
        var match = await Su3BalloonKit.CreateMatchAsync(
            pack,
            deckA: new[]
            {
                CommandTestKit.InfantryId, CommandTestKit.FighterId, CommandTestKit.InfantryId,
                CommandTestKit.BomberId, CommandTestKit.InfantryId,
            });
        var player = match.Players[0];
        var viewer = await Su3BalloonKit.GiveAsync(match, player, CommandTestKit.InfantryId);
        var runtime = player.EffectRuntime!;

        // 卡组顺序的第一张命中者（确定性、非随机）——先按同序求出预期。
        var expected = FirstMatch(player, card => card.Definition.UnitTypes.Any(
            t => t == UnitType.Fighter || t == UnitType.Bomber));
        Assert.NotNull(expected);

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await runtime.TutorFromDeckAsync(
            viewer, new[] { UnitType.Fighter, UnitType.Bomber }, viewer);

        Assert.Equal(TutorStatus.Fetched, result.Status);
        Assert.Same(expected, result.Card);
        Assert.Contains(result.Card!, player.Hand);
        Assert.False(player.Deck.ContainsInstance((CardBase)result.Card!));

        // 既有抽牌链路：card.drawn → card.hand.add（各恰一次）。
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardBurned));
    }

    [Fact]
    public async Task 谓词重载与数组版行为一致()
    {
        if (!Su3BalloonKit.TryLocate(out var su3Cards, out var balloonCards, out var su3Effects, out var balloonEffects))
        {
            return;
        }

        var pack = Su3BalloonKit.LoadPack(su3Cards, balloonCards, su3Effects, balloonEffects);

        var byArray = await FindAsync();
        var byPredicate = await FindAsync();

        Assert.Equal(TutorStatus.Fetched, byArray.Status);
        Assert.Equal(TutorStatus.Fetched, byPredicate.Status);
        Assert.Equal(((CardBase)byArray.Card!).Definition.Name, ((CardBase)byPredicate.Card!).Definition.Name);

        async Task<TutorResult> FindAsync(bool useArray = false)
        {
            var match = await Su3BalloonKit.CreateMatchAsync(
                pack,
                deckA: new[]
                {
                    CommandTestKit.InfantryId, CommandTestKit.FighterId, CommandTestKit.BomberId, CommandTestKit.InfantryId,
                });
            var player = match.Players[0];
            var viewer = await Su3BalloonKit.GiveAsync(match, player, CommandTestKit.InfantryId);
            var runtime = player.EffectRuntime!;

            return useArray
                ? await runtime.TutorFromDeckAsync(viewer, new[] { UnitType.Bomber }, viewer)
                : await runtime.TutorFromDeckAsync(viewer, static card => card is CardBase unitCard && unitCard.Definition.UnitTypes.Contains(UnitType.Bomber), viewer);
        }
    }

    [Fact]
    public async Task 无匹配_NotFound_零信号且指令照常跑完()
    {
        if (!Su3BalloonKit.TryLocate(out var su3Cards, out var balloonCards, out var su3Effects, out var balloonEffects))
        {
            return;
        }

        var pack = Su3BalloonKit.LoadPack(su3Cards, balloonCards, su3Effects, balloonEffects);
        // 卡组剩 3 张、其中无空军。
        var match = await Su3BalloonKit.CreateMatchAsync(
            pack,
            deckA: Enumerable.Repeat(CommandTestKit.InfantryId, 8)); // 起手 5 ⇒ 卡组剩 3 张（且其中无空军）
        var player = match.Players[0];
        var viewer = await Su3BalloonKit.GiveAsync(match, player, CommandTestKit.InfantryId);
        var runtime = player.EffectRuntime!;
        var deckBefore = player.Deck.Count;
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await runtime.TutorFromDeckAsync(
            viewer, new[] { UnitType.Fighter, UnitType.Bomber }, viewer);

        Assert.Equal(TutorStatus.NotFound, result.Status);
        Assert.Null(result.Card);
        Assert.Equal(deckBefore, player.Deck.Count);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
    }

    [Fact]
    public async Task 命中但满手_爆牌Burned且不授予()
    {
        if (!Su3BalloonKit.TryLocate(out var su3Cards, out var balloonCards, out var su3Effects, out var balloonEffects))
        {
            return;
        }

        var pack = Su3BalloonKit.LoadPack(su3Cards, balloonCards, su3Effects, balloonEffects);
        var match = await Su3BalloonKit.CreateMatchAsync(
            pack,
            deckA: new[] { CommandTestKit.FighterId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 7)));
        var player = match.Players[0];
        for (var i = 0; i < Orc.Game.Players.Player.HandLimit; i++)
        {
            await Su3BalloonKit.GiveAsync(match, player, CommandTestKit.InfantryId);
        }

        var viewer = player.Hand[0];
        var runtime = player.EffectRuntime!;
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await runtime.TutorFromDeckAsync(
            viewer, new[] { UnitType.Fighter, UnitType.Bomber }, viewer);

        Assert.Equal(TutorStatus.Burned, result.Status);
        Assert.NotNull(result.Card);
        Assert.False(result.Card!.Life.IsAlive);          // 已销毁（爆牌裁决）
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardBurned));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.DoesNotContain(result.Card, player.Hand);   // 没有凭空给一张已销毁的牌挂上效果

        // 反向断言：对已销毁的牌授予必须明确失败（不静默挂空壳）。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.AttachPrefabAsync(result.Card!, Su3BalloonKit.PinPrefabId));
    }

    // ==================== R6：动态效果授予 ====================

    [Fact]
    public async Task 授予预制体_同帧可见新边_撤销幂等()
    {
        Assert.True(DiyExampleKit.TryLocatePack(out var cardsDirectory, out var componentsDirectory));
        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, componentsDirectory);
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);

        var before = match.Engine.Model!.ComponentsOf(unit.ModelEntity!).Count;
        var handle = await runtime!.AttachPrefabAsync(unit, "diy.fire-support.effect");

        Assert.False(handle.IsRevoked);
        Assert.True(handle.Attachment.IsAssembled);
        Assert.Equal(before + 1, match.Engine.Model!.ComponentsOf(unit.ModelEntity!).Count);
        Assert.Contains(handle.Attachment, match.Engine.Model!.ComponentsOf(unit.ModelEntity!));

        // 幂等：第一次真撤销、第二次是空操作（不抛）。
        Assert.True(await runtime.DetachEffectAsync(handle));
        Assert.True(handle.IsRevoked);
        Assert.False(await runtime.DetachEffectAsync(handle));
    }

    [Fact]
    public async Task 授予内联快照_未注册也接受()
    {
        Assert.True(DiyExampleKit.TryLocatePack(out var cardsDirectory, out var componentsDirectory));
        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, componentsDirectory);
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);

        var snapshot = new EffectSnapshot(new EffectPrefab(
            "test.inline.grant",
            new TriggerPrefab(
                "test.inline.grant.t",
                "test.inline.grant.key",
                isPassive: true,
                hooks: new[] { GameUpdates.UnitActed })));
        Assert.False(match.Engine.Prefabs.TryGetPrefab("test.inline.grant", out _)); // 未注册

        var handle = await runtime!.AttachSnapshotAsync(unit, snapshot);

        Assert.True(match.Engine.Prefabs.TryGetPrefab("test.inline.grant", out _)); // 先注册后挂载
        Assert.True(handle.Attachment.IsAssembled);
        Assert.Equal("test.inline.grant", handle.PrefabId);
    }

    [Fact]
    public async Task 编译挂载_资产不可用时明确失败()
    {
        Assert.True(DiyExampleKit.TryLocatePack(out var cardsDirectory, out var componentsDirectory));
        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, componentsDirectory);
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);
        var runtime = EffectRuntime.ResolveFor(unit);
        var before = match.Engine.Model!.ComponentsOf(unit.ModelEntity!).Count;

        // 非法 DSL：必须明确失败，不能静默挂空壳。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime!.CompileAttachAsync(unit, "{ not-a-dsl }"));
        Assert.Equal(before, match.Engine.Model!.ComponentsOf(unit.ModelEntity!).Count);
    }

    [Fact]
    public async Task 授予期限UntilDestroyed_回手不撤销()
    {
        Assert.True(DiyExampleKit.TryLocatePack(out var cardsDirectory, out var componentsDirectory));
        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, componentsDirectory);
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);
        var runtime = EffectRuntime.ResolveFor(unit);
        var handle = await runtime!.AttachPrefabAsync(unit, "diy.fire-support.effect");

        // 非死亡离场（回手）不撤销：UntilDestroyed 只随宿主销毁撤销。
        await match.CommandManager.LeaveBattlefieldAsync(unit);
        Assert.False(handle.IsRevoked);
        Assert.False(handle.Attachment.IsDisposed);
    }

    [Fact]
    public async Task 授予期限UntilDestroyed_宿主销毁即撤销()
    {
        Assert.True(DiyExampleKit.TryLocatePack(out var cardsDirectory, out var componentsDirectory));
        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var match = await DiyExampleKit.CreateMatchAsync(pack, componentsDirectory);
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[0], CommandTestKit.InfantryId, 0);
        var runtime = EffectRuntime.ResolveFor(unit);
        var handle = await runtime!.AttachPrefabAsync(unit, "diy.fire-support.effect");

        // 宿主「销毁」＝编排实体销毁（死亡链本身只注销行为、不销毁实例）。
        unit.Destroy();

        Assert.False(unit.Life.IsAlive);
        Assert.True(handle.IsRevoked);
        Assert.True(handle.Attachment.IsDisposed);
    }

    // ==================== 工具 ====================

    private static CardBase? FirstMatch(Orc.Game.Players.Player player, Func<CardBase, bool> predicate)
    {
        for (var index = 0; index < player.Deck.Count; index++)
        {
            var candidate = player.Deck.PeekInstance(index);
            if (predicate(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ExtractValue(string manifest, string key)
    {
        var marker = $"\"{key}\": \"";
        var start = manifest.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = manifest.IndexOf('"', start);
        return manifest[start..end];
    }
}
