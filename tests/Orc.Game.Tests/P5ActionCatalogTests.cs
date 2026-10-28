using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Actions;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P5/S15 验收：动作目录（依赖序、每对局一份、装配期校验）与「按目录装配」语义。
/// </summary>
public class P5ActionCatalogTests
{
    [Fact]
    public async Task Catalog_Has_Eight_Actions_With_S10_Dependency_Edges()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        var catalog = match.CommandManager.Actions;
        Assert.Equal(
            new[] { "hand.add", "draw", "damage", "death", "attack", "move", "join.battlefield", "order.cast" },
            catalog.Entries.Select(entry => entry.Id.Value));

        // S10 依赖图：加入手牌 → 抽牌；伤害 → 攻击；死亡 → 加入手牌；其余无依赖
        AssertDependencies(catalog, ActionId.HandAdd);
        AssertDependencies(catalog, ActionId.Draw, ActionId.HandAdd);
        AssertDependencies(catalog, ActionId.Damage);
        AssertDependencies(catalog, ActionId.Death, ActionId.HandAdd);
        AssertDependencies(catalog, ActionId.Attack, ActionId.Damage);
        AssertDependencies(catalog, ActionId.Move);
        AssertDependencies(catalog, ActionId.JoinBattlefield);
        AssertDependencies(catalog, ActionId.Order);

        // 每动作恰一个处理端，且至少一个触发端
        Assert.All(catalog.Entries, entry =>
        {
            Assert.Equal(1, entry.HandleEndCount);
            Assert.True(entry.Ends.Count >= 2);
        });
    }

    [Fact]
    public async Task Catalog_Instances_Are_The_Ones_Exposed_By_CommandManager_And_Per_Match()
    {
        var first = GameTestData.CreateStandardMatch(seed: 42);
        await first.Initialize();
        var second = GameTestData.CreateStandardMatch(seed: 43);
        await second.Initialize();

        // 目录是实例装配真源：CommandManager 的公开动作面与目录同一实例（不再各自 new）
        Assert.Same(first.CommandManager.Actions.AddToHand, first.CommandManager.AddToHandAction);
        Assert.Same(first.CommandManager.Actions.Draw, first.CommandManager.DrawAction);
        Assert.Same(first.CommandManager.Actions.Damage, first.CommandManager.DamagePipeline.Action);
        Assert.Same(first.CommandManager.Actions.Death, first.CommandManager.DeathAction);
        Assert.Same(first.CommandManager.Actions.Attack, first.CommandManager.AttackAction);
        Assert.Same(first.CommandManager.Actions.Move, first.CommandManager.MoveAction);
        Assert.Same(first.CommandManager.Actions.JoinBattlefield, first.CommandManager.JoinBattlefieldAction);

        // 每对局一份：两局实例互不相同
        Assert.NotSame(first.CommandManager.Actions, second.CommandManager.Actions);
        Assert.NotSame(first.CommandManager.Actions.Damage, second.CommandManager.Actions.Damage);

        // P5b：八个动作的实现全部来自 orc.foundation.actions（宿主程序集不再包含动作实现）。
        Assert.IsType<Orc.Foundation.Actions.AddToHandAction>(first.CommandManager.Actions.AddToHand);
        Assert.IsType<Orc.Foundation.Actions.DrawAction>(first.CommandManager.Actions.Draw);
        Assert.IsType<Orc.Foundation.Actions.DamageAction>(first.CommandManager.Actions.Damage);
        Assert.IsType<Orc.Foundation.Actions.DeathAction>(first.CommandManager.Actions.Death);
        Assert.IsType<Orc.Foundation.Actions.AttackAction>(first.CommandManager.Actions.Attack);
        Assert.IsType<Orc.Foundation.Actions.MoveAction>(first.CommandManager.Actions.Move);
        Assert.IsType<Orc.Foundation.Actions.JoinBattlefieldAction>(first.CommandManager.Actions.JoinBattlefield);
        Assert.IsType<Orc.Foundation.Actions.OrderAction>(first.CommandManager.Actions.Order);
    }

    [Fact]
    public void Catalog_Fails_Fast_On_Duplicate_Id()
    {
        var entries = new[]
        {
            Entry(ActionId.Damage),
            Entry(ActionId.Damage),
        };

        var error = Assert.Throws<InvalidOperationException>(() => MatchActionCatalog.Validate(entries));
        Assert.Contains("重复", error.Message);
    }

    [Fact]
    public void Catalog_Fails_Fast_On_Missing_Dependency()
    {
        var entries = new[] { Entry(ActionId.Attack, ActionId.Damage) };

        var error = Assert.Throws<InvalidOperationException>(() => MatchActionCatalog.Validate(entries));
        Assert.Contains("缺依赖", error.Message);
    }

    [Fact]
    public void Catalog_Fails_Fast_On_Cycle()
    {
        var entries = new[]
        {
            Entry(ActionId.Damage, ActionId.Attack),
            Entry(ActionId.Attack, ActionId.Damage),
        };

        var error = Assert.Throws<InvalidOperationException>(() => MatchActionCatalog.Validate(entries));
        Assert.Contains("成环", error.Message);
    }

    private static void AssertDependencies(MatchActionCatalog catalog, ActionId id, params ActionId[] expected)
    {
        var entry = Assert.Single(catalog.Entries, item => item.Id == id);
        Assert.Equal(expected, entry.DependsOn);
    }

    private static MatchActionEntry Entry(ActionId id, params ActionId[] dependencies)
        => new(
            id,
            new object(),
            dependencies,
            new[]
            {
                new ActionTriggerEnd("触发端", MemberAddress.Create(AddressScope.Engine, "test.trigger")),
                new ActionTriggerEnd(ActionTriggerEnd.HandleRole, MemberAddress.Create(AddressScope.Engine, "test.handle")),
            });
}
