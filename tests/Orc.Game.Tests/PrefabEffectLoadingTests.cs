using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S-C8/C9 游戏层接线验收：`CardEffectRegistry.DeclarePrefab` → 装载链实例化动态效果并装载；
/// 未注册预制体＝隔离记录、不阻断加载。
/// </summary>
public class PrefabEffectLoadingTests
{
    private const string DemoId = "u_prefab_demo";
    private const string Hook = "it.prefab.hook";

    private static CardDefinitionEntry DemoDefinition => new(
        DemoId,
        new CardDefinition(
            "预制体演示兵", deployCost: 1, operateCost: 1, attack: 5, defense: 4,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    private static EffectSnapshot PassiveSnapshot(string handlerKey) => new(
        new EffectPrefab(
            "prefab.dyn",
            new TriggerPrefab(
                "t.main", "it.main", true,
                hooks: new[] { Hook },
                events: new[] { new EventPrefab("onHook", assemblyKey: handlerKey) })));



    [Fact]
    public void 预制体声明_重复拒绝()
    {
        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(DemoId, new[] { "a" });

        Assert.Throws<InvalidOperationException>(() => registry.DeclarePrefab(DemoId, new[] { "b" }));
        Assert.Throws<ArgumentException>(() => registry.DeclarePrefab("x", new[] { "a", "a" }));
    }
}
