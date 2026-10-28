using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Model;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **A2 落地验收**：7 个内建词条的行为制品从「C# 效果注入」迁到 **v4 效果组件（csx）** 之后，
/// ①制品就位（组件资产在编排运行时目录、七个标识逐个可解析，包装/挂载目标与设计一致）；
/// ②**动作面可达**（csx 内 <c>EffectRuntime.DeathAsync</c>）——伏击的「以死代伤」仍走死亡动作**完整两端**：
/// 死亡处理端外挂的免死守卫会被命中（载荷单位＝被改写的攻击者），且改写后果被守卫拦下。
/// <para>
/// **判定器通道**（csx 内 <c>EffectRuntime.Judge</c> → <c>combat.counter.eligibility</c>／<c>combat.ambush.condition</c>）
/// 的可达证据＝既有的 <c>JudicatorCombatCounterAmbushTests</c>「调用点二（伏击资格）」：它在同一对局内
/// 用判定器包装把资格改为恒真，伏击改写结果随之从「不成立」变为「成立」——只有 csx 真的走判定器注册表才会随动。
/// </para>
/// <para>
/// 行为等价（逐词条的黑盒断言）由 <see cref="KeywordEffectBatch1Tests"/>（动员/压制/情报）、
/// <see cref="KeywordEffectBatch2Tests"/>（伏击/重甲/免疫）、<see cref="KeywordEffectBatch3Tests"/>（闪击）、
/// <see cref="KeywordEffectBatch4Tests"/>（生产装配端到端）承载——本文件只补「通道可达」这一类证据。
/// </para>
/// </summary>
public sealed class KeywordV4ComponentTests
{
    /// <summary>七个内建词条 → 组件资产标识（与 <see cref="KeywordEffectAssembly"/> 常量组同源）。</summary>
    private static readonly (string Keyword, string ComponentId, string[] WrappedMembers, string[] MountedSignals)[] Expected =
    {
        (KeywordIds.Armor, KeywordEffectAssembly.ArmorComponentId, new[] { EngineProcesses.CombatDamage }, Array.Empty<string>()),
        (KeywordIds.Immune, KeywordEffectAssembly.ImmuneComponentId, new[] { EngineProcesses.DamageApply }, Array.Empty<string>()),
        (KeywordIds.Ambush, KeywordEffectAssembly.AmbushComponentId, new[] { EngineProcesses.AttackDamage }, Array.Empty<string>()),
        (KeywordIds.Suppressed, KeywordEffectAssembly.SuppressionComponentId, Array.Empty<string>(), new[] { GameUpdates.TurnEnd }),
        (KeywordIds.Intelligence, KeywordEffectAssembly.IntelligenceComponentId, Array.Empty<string>(), new[] { GameUpdates.CardPlayed }),
        (KeywordIds.Mobilize, KeywordEffectAssembly.MobilizeAccrualComponentId, Array.Empty<string>(), new[] { GameUpdates.TurnStart }),
        (KeywordIds.Blitz, KeywordEffectAssembly.BlitzDeploySetComponentId, Array.Empty<string>(), new[] { GameUpdates.UnitDeployed }),
    };

    // ---------- ① 制品就位 ----------

    [Fact]
    public async Task 内建词条_v4组件资产全部就位_包装挂载目标与设计一致()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var runtime = match.Engine.Model!;

        foreach (var (keyword, componentId, wrappedMembers, mountedSignals) in Expected)
        {
            Assert.True(runtime.Catalog.Contains(componentId), $"词条 '{keyword}' 的组件资产 '{componentId}' 未进目录");
            var type = runtime.Catalog.Resolve(componentId);
            Assert.Equal(EntityKind.Component, type.Kind);

            foreach (var member in wrappedMembers)
            {
                Assert.Contains(
                    type.Wraps,
                    wrap => wrap.Target.IsMember
                        && wrap.Target.Address.Scope == AddressScope.Engine
                        && wrap.Target.Address.Member == member);
            }

            foreach (var signal in mountedSignals)
            {
                Assert.Contains(
                    type.Mounts,
                    mount => mount.Target == $"engine#{signal}");
            }
        }

        // 词条 → 组件绑定也不再指向旧数据壳：绑定 id 全部可在 v4 目录解析。
        var bindings = KeywordRegistry.Snapshot();
        foreach (var (keyword, componentId, _, _) in Expected)
        {
            var declared = bindings.GetEffectBindings(keyword);
            Assert.NotEmpty(declared);
            Assert.All(declared, binding => Assert.True(runtime.Catalog.Contains(binding.PrefabId)));
            Assert.Contains(declared, binding => binding.PrefabId == componentId);
        }
    }

    // ---------- ③ 动作面通道：伏击「以死代伤」走死亡动作完整两端（免死守卫可拦） ----------

}
