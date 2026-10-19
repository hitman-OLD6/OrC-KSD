using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 4（R6d）证据：**每有一个敌方轰炸机，本单位 +1 攻击**——代码侧自定义 `AuraDeclaration`
/// （transform 里**现数**敌方轰炸机），随入场/死亡的全域重跑自动随动。
/// 同时说明：`EffectRuntime.DeclareAuraAsync` 的 delta 是静态值，无法表达「每有一个」。
/// </summary>
public class DiyExample04_BomberAuraTests
{
    [Fact]
    public async Task 轰炸机计数光环_随敌方轰炸机入场与阵亡随动()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 宿主：步兵（基础攻击 2），效果在入场前装载（真实卡按加载时点装载）。
        var host = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId);
        host.AddEffect(new BomberCountAuraEffect());
        var join = await match.PlayManager.JoinUnitAsync(host, DiyExampleKit.FindEmptySupportSlot(match, playerA));
        Assert.Equal(Orc.Game.Cards.PlayResultStatus.Success, join.Status);
        Assert.Equal(2, host.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 敌方第一架轰炸机 → +1。
        var bomber1 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 0);
        Assert.Equal(3, host.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 第二架 → +2（若用静态 delta，此处仍是 +1）。
        var bomber2 = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.BomberId, 1);
        Assert.Equal(4, host.Modifiers.GetEffectiveValue(CardStatFields.Attack));

        // 一架阵亡 → 回到 +1。
        var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(bomber1);
        Assert.NotNull(runtime);
        Assert.True(await runtime!.KillAsync(bomber1));
        Assert.Equal(3, host.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        Assert.NotNull(bomber2);
    }

    /// <summary>自定义光环：transform 每次重跑现数「敌方轰炸机」，直接加进攻击字段。</summary>
    private sealed class BomberCountAuraEffect : PassiveEffect
    {
        public BomberCountAuraEffect()
            : base("轰炸机计数光环")
        {
        }

        protected override void OnMount()
        {
            var environment = GameEnvironment.ResolveFor(Host);
            if (environment is null)
            {
                return; // 脱局/未装配：不注册（降级不抛）。
            }

            var declaration = new AuraDeclaration(
                Host,
                CardStatFields.Attack,
                (_, current) => current + CountEnemyBombers(Host, environment),
                this,
                (_, beneficiary) => ReferenceEquals(beneficiary, Host));

            environment.Auras.Register(declaration);
            AddTeardownRegistration(() => environment.Auras.RemoveBySource(this));
        }

        private static int CountEnemyBombers(Card host, GameEnvironment environment)
        {
            if ((host as CardBase)?.Owner is not { } owner)
            {
                return 0;
            }

            var count = 0;
            foreach (var line in new[]
                     {
                         environment.Battlefield.PlayerASupportLine,
                         environment.Battlefield.PlayerBSupportLine,
                         environment.Battlefield.FrontLine,
                     })
            {
                foreach (var slot in line)
                {
                    if (slot.Occupant is UnitCard unit
                        && !ReferenceEquals(unit.Owner, owner)
                        && unit.Definition.UnitTypes.Contains("bomber"))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }
}
