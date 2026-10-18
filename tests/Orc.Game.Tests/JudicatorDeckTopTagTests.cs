using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// J3 示范①（卡组顶特点判定器）示范场景测试：
/// ① 默认求值——读取真实卡组顶（TagData 开放 tag；有/无特点、大小写逐字、null/空白标识）；
/// ② 空卡组＝false 降级（不抛错）；未加载态＝明确错误（可构造则验）；
/// ③ 恒真改写（多底态多次求值稳定——整体替换覆盖降级）→ 注销回退；
/// ④ 恒假改写（两态之二）→ 注销回退；
/// ⑤ 玩家 null＝fail-fast（参数契约错误）。
/// 链路口径（需求 Q&amp;A-2 1③）：全部求值经正常引用面（注册面按名解析→调用判定器；不直连判定器实例）——
/// 覆盖「引用-调用-改写」完整链；「全局生效」＝单引用点行为差异＋机制级背书（J1/J2 机制测试）。
/// 对应验收：①（默认求值〔含无该特点实况〕、无顶卡 false 降级）②（恒真/恒假改写两态、注销回退）。
/// </summary>
public class JudicatorDeckTopTagTests
{
    private const string PlainId = "dt_plain";
    private const string TaggedId = "dt_tagged";

    // ---------- ① 默认求值：读取真实卡组顶 ----------

    [Fact]
    public async Task Default_Evaluation_Reads_Real_Deck_Top_Tag_Presence()
    {
        var match = CreateDemoMatch();
        await match.Initialize();
        var player = match.Players[0];

        DrainDeck(player);
        await InsertTopAsync(match, player, TaggedId); // 卡组顶＝有特点卡（开放 tag "navy"）

        Assert.True(Evaluate(match, player, "navy"));   // 有该特点
        Assert.False(Evaluate(match, player, "NAVY"));  // ordinal 逐字相等（大小写敏感）
        Assert.False(Evaluate(match, player, "army"));  // 无该特点（实况）
        Assert.False(Evaluate(match, player, null));    // 标识 null＝false（存在性查询口径、不抛错）
        Assert.False(Evaluate(match, player, "   "));   // 标识空白＝false

        await InsertTopAsync(match, player, PlainId);   // 换成无特点卡到顶
        Assert.False(Evaluate(match, player, "navy"));  // 读的是真顶卡（顶卡无特点）
        Assert.Equal(2, player.Deck.Count);             // 只读读取不改计数（两次插入后 2 条）
    }

    // ---------- ② 空卡组降级 / 未加载态明确错误 ----------

    [Fact]
    public async Task Empty_Deck_Falls_Back_False_And_Unloaded_Top_Is_Explicit_Error()
    {
        var match = CreateDemoMatch();
        await match.Initialize();
        var player = match.Players[0];

        DrainDeck(player);
        Assert.Empty(player.Deck);
        Assert.False(Evaluate(match, player, "navy")); // 空卡组＝不满足（false 降级、不抛错）
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Deck.PeekInstance(0)); // 读取面越界（空集合）＝明确错误（容器风格）

        player.Deck.Add(PlainId); // 顶条目存在但未装配加载实例（未加载态）
        Assert.Throws<InvalidOperationException>(() => Evaluate(match, player, "navy")); // 明确错误（不归 false 降级）
    }

    // ---------- ③ 恒真改写（多底态稳定）＋注销回退 ----------

    [Fact]
    public async Task Constant_True_Moding_Holds_Across_States_And_Falls_Back()
    {
        var match = CreateDemoMatch();
        await match.Initialize();
        var player = match.Players[0];

        DrainDeck(player);
        Assert.False(Evaluate(match, player, "navy")); // 空卡组基线（降级 false）

        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.DeckTopTag),
            _ => new object[] { true });
        Assert.NotNull(moding);

        // 「恒」的证据：多底态多次求值稳定（空卡组〔整体替换覆盖降级〕/有特点卡/无特点卡）
        Assert.True(Evaluate(match, player, "navy"));     // 空卡组底态
        await InsertTopAsync(match, player, TaggedId);
        Assert.True(Evaluate(match, player, "navy"));     // 有特点卡底态
        Assert.True(Evaluate(match, player, "anything")); // 任意标识亦 true（整体替换——非单次巧合）
        await InsertTopAsync(match, player, PlainId);
        Assert.True(Evaluate(match, player, "navy"));     // 无特点卡底态
        Assert.True(Evaluate(match, player, "navy"));     // 重复求值稳定

        // 注销回退（经同一路径）
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.False(Evaluate(match, player, "navy"));    // 顶＝无特点卡 → false（默认逻辑恢复）
        player.Deck.DrawInstance();                       // 移掉顶卡（无特点卡）
        Assert.True(Evaluate(match, player, "navy"));     // 顶＝有特点卡 → true（回退后按真实底态求值）
    }

    // ---------- ④ 恒假改写（两态之二）＋注销回退 ----------

    [Fact]
    public async Task Constant_False_Moding_Holds_And_Falls_Back()
    {
        var match = CreateDemoMatch();
        await match.Initialize();
        var player = match.Players[0];

        DrainDeck(player);
        await InsertTopAsync(match, player, TaggedId);
        Assert.True(Evaluate(match, player, "navy")); // 有特点卡基线（默认 true）

        var moding = match.Judicators.RegisterReplaceWrapper(
            match.Judicators.Resolve(JudicatorNames.DeckTopTag),
            _ => new object[] { false });
        Assert.NotNull(moding);

        // 「恒」的证据：多底态多次求值稳定
        Assert.False(Evaluate(match, player, "navy")); // 有特点卡底态
        await InsertTopAsync(match, player, TaggedId); // 再叠一张有特点卡（另一底态）
        Assert.False(Evaluate(match, player, "navy"));
        DrainDeck(player);                             // 空卡组底态（降级路径亦被整体替换覆盖）
        Assert.False(Evaluate(match, player, "navy"));

        // 注销回退（经同一路径）
        Assert.True(match.Judicators.UnregisterWrapper(moding!));
        Assert.False(Evaluate(match, player, "navy")); // 空卡组 → false（降级恢复）
        await InsertTopAsync(match, player, TaggedId);
        Assert.True(Evaluate(match, player, "navy"));  // 有特点卡 → true（默认求值恢复）
    }

    // ---------- ⑤ 玩家 null＝fail-fast ----------

    [Fact]
    public async Task Null_Player_Fails_Fast()
    {
        var match = CreateDemoMatch();
        await match.Initialize();

        var registration = match.Judicators.Resolve(JudicatorNames.DeckTopTag);
        Assert.Throws<ArgumentNullException>(() => registration.Invoke(new object[] { null!, "navy" }));
    }

    // ---------- 测试辅助 ----------

    /// <summary>创建示范①对局（卡组＝普通卡 x10；装配期注册卡组顶特点判定器——经既有装配期注册面）。</summary>
    private static Match CreateDemoMatch()
        => new(
            new CardList(Enumerable.Repeat(PlainId, 10)),
            new CardList(Enumerable.Repeat(PlainId, 10)),
            CreateDemoDefinitions(),
            seed: 42,
            judicatorAssembly: registry => registry.Register(JudicatorNames.DeckTopTag, new DeckTopTagJudicator()),
            packageActivator: TestHost.Activator);

    private static IReadOnlyList<CardDefinitionEntry> CreateDemoDefinitions() => new[]
    {
        new CardDefinitionEntry(
            PlainId,
            new CardDefinition("无特点卡", 1, 1, 2, 3, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(
            TaggedId,
            new CardDefinition("有特点卡", 1, 1, 2, 3, faction: Faction.Germany, rarity: Rarity.Standard, tags: new[] { "navy" })),
    };

    /// <summary>掏空卡组（测试构造辅助——逐张取件移除；实例已加载）。</summary>
    private static void DrainDeck(Player player)
    {
        while (player.Deck.Count > 0)
        {
            player.Deck.DrawInstance();
        }
    }

    /// <summary>插入一张实例化＋加载完成的卡到卡组顶（索引 0；测试构造辅助——可控底态）。</summary>
    private static async Task<CardBase> InsertTopAsync(Match match, Player player, string id)
    {
        var card = (CardBase)match.CardLibrary.Instantiate(id);
        await card.LoadAsync(player);
        player.Deck.InsertInstanceAt(0, id, card);
        return card;
    }

    /// <summary>经正常引用面求值：注册面按名解析 → 调用判定器 → 解释统一面输出（示范消费者）。</summary>
    private static bool Evaluate(Match match, Player player, string? tag)
    {
        var registration = match.Judicators.Resolve(JudicatorNames.DeckTopTag);
        var raw = registration.Invoke(new object[] { player, tag! }); // tag null 合法（存在性查询口径）
        Assert.NotNull(raw);
        Assert.Single(raw!);
        return Assert.IsType<bool>(raw![0]);
    }
}
