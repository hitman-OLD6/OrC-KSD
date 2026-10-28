using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Damage;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// R11 伤害三层管道验收：三触发端（combat/effect/battle）→ 一处理端（damage.apply）→ 唯一门户。
/// 覆盖：三端改写被处理端读到、免疫覆盖全部三类、重甲只减对战、门户调用点唯一（源码扫描）。
/// </summary>
public class DamagePipelineTests
{
    private const string ArmorTwoId = "u_r11_armor2";
    private const string ImmuneId = "u_r11_immune";

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


    /// <summary>
    /// 门户调用点唯一：<c>src/</c> 内只有动作服务实现 <c>Actions/MatchActionServices.cs</c> 调两个数值门户
    /// （S5a/P5：实体已迁入服务实现，<c>DamagePipeline</c> 的旧转发入口已删除，管道只经伤害动作门面）；
    /// UI/MCP/samples 零调用（外部程序集另受 <c>internal</c> 编译期约束）。
    /// </summary>
    [Fact]
    public void Pipeline_Apply_Is_Only_Portal_Caller()
    {
        var root = DiyExampleKit.FindRepoRoot();
        var callers = EnumerateSources(Path.Combine(root, "src"))
            .Where(file => Reads(file, ".ApplyDefenseDamageAsync(") || Reads(file, "hq.ApplyDamageAsync("))
            .Select(file => Relative(root, file))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "src/Orc.Game/Actions/MatchActionServices.cs" }, callers);

        foreach (var external in new[] { "src/Orc.UI", "src/Orc.Mcp", "src/Orc.Ai", "src/Orc.Plugins.BuiltIn", "samples" })
        {
            var directory = Path.Combine(root, external);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var hits = EnumerateSources(directory)
                .Where(file => Reads(file, ".ApplyDefenseDamageAsync(") || Reads(file, "hq.ApplyDamageAsync("))
                .Select(file => Relative(root, file))
                .ToArray();
            Assert.Empty(hits);
        }
    }

    [Fact]
    public async Task Effect_Damage_Is_Zeroed_By_Immune()
    {
        // R11-note 行为变更：此前「效果伤害穿透免疫」，现由 damage.apply 归零。
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var immune = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ImmuneId, 1); // 防 5
        var source = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);

        var runtime = EffectRuntime.ResolveFor(immune)!;
        Assert.True(await runtime.DamageAsync(immune, 3, source));

        Assert.Equal(5, DefenseOf(immune)); // 归零：0 伤害
    }

    [Fact]
    public async Task Armor_Does_Not_Reduce_Effect_Damage()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var armored = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ArmorTwoId, 1); // 重甲 2 / 防 5
        var source = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);

        var runtime = EffectRuntime.ResolveFor(armored)!;
        Assert.True(await runtime.DamageAsync(armored, 3, source));

        Assert.Equal(2, DefenseOf(armored)); // 5-3：重甲只挂 combat.damage
    }

    [Fact]
    public async Task Battle_Damage_Is_Zeroed_By_Immune()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var immune = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ImmuneId, 1); // 攻 2 / 防 5
        var rival = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2); // 攻 2 / 防 5

        var runtime = EffectRuntime.ResolveFor(immune)!;
        Assert.True(await runtime.BattleAsync(immune, rival));

        Assert.Equal(5, DefenseOf(immune)); // 战斗伤害同样被免疫归零
        Assert.Equal(3, DefenseOf(rival));  // 免疫方照常造成伤害（5-2）
    }

    [Fact]
    public async Task Armor_Does_Not_Reduce_Battle_Damage()
    {
        var match = CommandTestKit.CreateCommandMatch(extraDefinitions: ExtraDefinitions);
        await match.Initialize();
        var playerA = match.Players[0];
        var armored = await CommandTestKit.PrepareOnSupportAsync(match, playerA, ArmorTwoId, 1); // 攻 2 / 防 5（重甲 2）
        var rival = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2); // 攻 2 / 防 5

        var runtime = EffectRuntime.ResolveFor(armored)!;
        Assert.True(await runtime.BattleAsync(armored, rival));

        Assert.Equal(3, DefenseOf(armored)); // 5-2：战斗伤害不减免（重甲只减对战）
        Assert.Equal(3, DefenseOf(rival));   // 5-2
    }

    private static int DefenseOf(Card card) => ((CardBase)card).Modifiers.GetEffectiveValue(CardStatFields.Defense);

    private static IEnumerable<string> EnumerateSources(string directory)
        => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static bool Reads(string file, string token)
        => File.ReadAllText(file).Contains(token, StringComparison.Ordinal);

    private static string Relative(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');
}
