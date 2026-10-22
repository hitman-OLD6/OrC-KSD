using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Mcp.Contracts;
using Orc.Mcp.Room;
using Orc.Server.Rooms;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S2b 卡组规则三档守卫：<c>lenient</c> 只放宽张数/同名上限/配国，**保留**卡池权威（blank-id / unknown-card）；
/// AI 卡组在 lenient 下尽力生成、绝不返回空且同种子可复现；房间态校验与建局期校验共用同一档位口径。
/// </summary>
public class DeckRuleTierTests
{
    private const int RequiredSize = DeckRules.DefaultRequiredSize;

    /// <summary>strict＝生产口径逐项校验；lenient 只保留空白 id 与卡池存在性。</summary>
    [Fact]
    public void Lenient_RelaxesSizeAndCopyLimits_ButKeepsCardPoolAuthority()
    {
        var pool = BuildPool();

        // 3 张小卡组：标准 5 张（超 4 张上限）、精英 2 张（超 1 张上限）、空 id、未知卡。
        var deck = new[]
        {
            "std", "std", "std", "std", "std",
            "elite", "elite",
            "",
            "ghost",
        };

        var strict = DeckRules.Validate(deck, pool, RequiredSize, DeckRuleTier.Strict);
        Assert.Contains(strict, v => v.Kind == "size");
        Assert.Contains(strict, v => v.Kind == "copy-limit" && v.CardId == "std");
        Assert.Contains(strict, v => v.Kind == "copy-limit" && v.CardId == "elite");
        Assert.Contains(strict, v => v.Kind == "blank-id");
        Assert.Contains(strict, v => v.Kind == "unknown-card" && v.CardId == "ghost");

        var lenient = DeckRules.Validate(deck, pool, RequiredSize, DeckRuleTier.Lenient);
        Assert.DoesNotContain(lenient, v => v.Kind == "size");
        Assert.DoesNotContain(lenient, v => v.Kind == "copy-limit");
        // 卡池权威是上游 R12 硬边界：lenient 不得放过。
        Assert.Contains(lenient, v => v.Kind == "blank-id");
        Assert.Contains(lenient, v => v.Kind == "unknown-card" && v.CardId == "ghost");
    }

    /// <summary>配国校验同样分档：strict 拒绝矛盾配置，lenient 不拒绝；未提供配置一律通过。</summary>
    [Fact]
    public void Lenient_SkipsFactionPairing_ButStrictRejects()
    {
        var contradictory = new PlayerDeckConfiguration("not-a-major", "also-not-a-major");

        Assert.NotNull(DeckRules.ValidateFaction(contradictory, DeckRuleTier.Strict));
        Assert.Null(DeckRules.ValidateFaction(contradictory, DeckRuleTier.Lenient));
        Assert.Null(DeckRules.ValidateFaction(null, DeckRuleTier.Strict));
    }

    /// <summary>
    /// 小卡包（容量 &lt; 39）下：lenient 尽力生成非空且同种子确定；strict 仍返回空。
    /// <para>
    /// <c>relaxCopyLimit</c>（lenient 档）放宽同名上限后，小卡包也能凑满 39 张——否则长局会因牌库
    /// 过早抽空触发引擎“空集合 Draw 被拒绝”。
    /// </para>
    /// </summary>
    [Fact]
    public void AiDeckGenerator_BestEffort_ReturnsNonEmptyAndDeterministic()
    {
        var pool = LoadSamplePool();

        var first = new AiDeckGenerator(pool, new Random(7)).Generate(RequiredSize, bestEffort: true);
        var second = new AiDeckGenerator(pool, new Random(7)).Generate(RequiredSize, bestEffort: true);

        Assert.NotEmpty(first);
        Assert.True(first.Count < RequiredSize, "按同名上限时示例卡包容量不足以凑满 39 张。");
        Assert.Equal(first, second); // 同卡包 ＋ 同种子 → 同一卡组
        Assert.All(first, id => Assert.True(pool.ContainsKey(id), $"'{id}' 不在卡池中。"));

        // lenient（放宽同名上限）：同一小卡包可凑满目标张数，且仍然确定。
        var relaxed = new AiDeckGenerator(pool, new Random(7)).Generate(
            RequiredSize, bestEffort: true, relaxCopyLimit: true);
        var relaxedAgain = new AiDeckGenerator(pool, new Random(7)).Generate(
            RequiredSize, bestEffort: true, relaxCopyLimit: true);

        Assert.Equal(RequiredSize, relaxed.Count);
        Assert.Equal(relaxed, relaxedAgain);
        Assert.All(relaxed, id => Assert.True(pool.ContainsKey(id), $"'{id}' 不在卡池中。"));

        // strict 口径（bestEffort=false）不得用不足张数的卡组冒充合法构筑。
        Assert.Empty(new AiDeckGenerator(pool, new Random(7)).Generate(RequiredSize));
    }

    /// <summary>
    /// 批准期（S4：卡组校验前移到批准这一刻）按 lenient 档位判定：小卡组放行、未知卡仍拒绝。
    /// <para>服务器启动时不装卡池，因此校验必须自己带上"用并集算出的池视图"。</para>
    /// </summary>
    [Fact]
    public void ApprovalValidation_UsesLenientTier_SmallDeckAccepted_UnknownCardRejected()
    {
        var pool = BuildPool();
        var validator = new DeckRulesValidator(() => pool, enforce: true, requiredDeckSize: 0, tier: DeckRuleTier.Lenient);

        Assert.Null(validator.Validate(["std", "elite"], null));

        var unknown = validator.Validate(["std", "ghost"], null);
        Assert.NotNull(unknown);
        Assert.Equal(McpCodes.DeckInvalid, unknown!.Code);
        Assert.Contains("ghost", unknown.Message);
    }

    private static IReadOnlyDictionary<string, CardDefinition> BuildPool()
        => new Dictionary<string, CardDefinition>(StringComparer.Ordinal)
        {
            ["std"] = new("标准", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard),
            ["elite"] = new("精英", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Elite),
        };

    private static IReadOnlyDictionary<string, CardDefinition> LoadSamplePool()
    {
        var assembly = Orc.Packages.Loading.MatchCardAssembly.Register([SamplePackDirectory()]);
        return assembly.Definitions.ToDictionary(
            entry => entry.Id,
            entry => entry.Definition,
            StringComparer.Ordinal);
    }

    private static string SamplePackDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }
}
