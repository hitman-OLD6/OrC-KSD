using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Collections;
using Orc.Game.Players;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// W1-1 验收（G12 卡牌元数据维度）：TagData 组件（必填强类型槽位＋开放 tag 增删查）、
/// 定义期声明（fail-fast：缺失必填/未定义枚举值/空白与重复 tag）、加载装配链（三类覆盖、先于 card.load 广播、未加载读取明确错误）、
/// 对局级 ID 水位线构筑外判定（初始化内 / 构筑外生成模拟 / 未加载拒绝 / 重复加载同 ID）。
/// 四验收场景：①组合筛选（国籍×稀有度×子类别）②开放子类别查询（海军/T-34；同集合多值 AND）
/// ③构筑外判定（初始化内 vs 构筑外生成牌模拟）④开放 tag 运行时增删生效（同实例即时读回＋筛选随动）。
/// S10 随改（受控适配）：国籍自 TagData 移出——国籍相关断言改读合并组件 <see cref="FactionCostData"/>
/// （「阵营〔国籍〕＋部署费」；读面与受控写面的完整验证见 FactionCostComponentTests）；稀有度/开放 tag 断言不变。
/// </summary>
public class CardTagDataTests
{
    // ---------- 样本定义 id（与 CreateDefinitions 一一对应） ----------

    /// <summary>日本精英空军（Japan / Elite / 空军＋海军——场景①「日本精英空军」命中项）。</summary>
    private const string JpEliteAirId = "mt_jp_elite_air";

    /// <summary>日本精英陆军（Japan / Elite / 陆军——场景①对照：同国同稀有度、无空军）。</summary>
    private const string JpEliteGroundId = "mt_jp_elite_ground";

    /// <summary>日本基础空军（Japan / Standard / 空军——场景①对照：同国有空军、非精英；亦作对局卡组填充卡）。</summary>
    private const string JpStdAirId = "mt_jp_std_air";

    /// <summary>德国精英空军（Germany / Elite / 空军——场景①对照：同稀有度同子类别、非日本）。</summary>
    private const string GerEliteAirId = "mt_ger_elite_air";

    /// <summary>T-34 坦克（Soviet / Standard / 海军＋T-34——场景②样本）。</summary>
    private const string SuT34Id = "mt_su_t34";

    /// <summary>谢尔曼（USA / Limited / 海军＋谢尔曼——场景②样本）。</summary>
    private const string UsShermanId = "mt_us_sherman";

    /// <summary>皇家海军（Britain / Special / 海军——场景②样本）。</summary>
    private const string UkNavyId = "mt_uk_navy";

    /// <summary>法国白板（France / Standard / 无开放 tag——场景②④对照：不命中任何子类别）。</summary>
    private const string FrPlainId = "mt_fr_plain";

    /// <summary>空中侦察（指令卡 / Japan / Limited / 空军——三类覆盖之指令）。</summary>
    private const string CmdAirId = "mt_cmd_air";

    /// <summary>反潜戒备（反制卡 / Britain / Special / 海军——三类覆盖之反制）。</summary>
    private const string CntNavyId = "mt_cnt_navy";

    /// <summary>样本 id 序列（场景①②④的加载集合顺序）。</summary>
    private static readonly string[] SampleIds =
    {
        JpEliteAirId, JpEliteGroundId, JpStdAirId, GerEliteAirId, SuT34Id,
        UsShermanId, UkNavyId, FrPlainId, CmdAirId, CntNavyId,
    };

    /// <summary>
    /// 元数据样本定义集（10 枚）：覆盖 国籍×稀有度×子类别 组合矩阵、三类卡、无 tag 卡；
    /// 「海军 / T-34 / 谢尔曼」示例值取自需求（场景②）。
    /// </summary>
    private static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(JpEliteAirId, new CardDefinition("日本精英空军", 1, 1, 3, 2,
            unitTypes: new[] { UnitType.Fighter }, faction: Faction.Japan, rarity: Rarity.Elite, tags: new[] { "空军", "海军" })),
        new CardDefinitionEntry(JpEliteGroundId, new CardDefinition("日本精英陆军", 2, 1, 4, 4,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Japan, rarity: Rarity.Elite, tags: new[] { "陆军" })),
        new CardDefinitionEntry(JpStdAirId, new CardDefinition("日本基础空军", 1, 1, 2, 2,
            unitTypes: new[] { UnitType.Fighter }, faction: Faction.Japan, rarity: Rarity.Standard, tags: new[] { "空军" })),
        new CardDefinitionEntry(GerEliteAirId, new CardDefinition("德国精英空军", 2, 1, 3, 3,
            unitTypes: new[] { UnitType.Fighter }, faction: Faction.Germany, rarity: Rarity.Elite, tags: new[] { "空军" })),
        new CardDefinitionEntry(SuT34Id, new CardDefinition("T-34坦克", 3, 1, 4, 4,
            unitTypes: new[] { UnitType.Tank }, faction: Faction.Soviet, rarity: Rarity.Standard, tags: new[] { "海军", "T-34" })),
        new CardDefinitionEntry(UsShermanId, new CardDefinition("谢尔曼", 3, 1, 3, 4,
            unitTypes: new[] { UnitType.Tank }, faction: Faction.USA, rarity: Rarity.Limited, tags: new[] { "海军", "谢尔曼" })),
        new CardDefinitionEntry(UkNavyId, new CardDefinition("皇家海军", 2, 1, 2, 5,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Britain, rarity: Rarity.Special, tags: new[] { "海军" })),
        new CardDefinitionEntry(FrPlainId, new CardDefinition("法国白板", 1, 1, 2, 3,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.France, rarity: Rarity.Standard)),
        new CardDefinitionEntry(CmdAirId, new CardDefinition("空中侦察", 1, 0, 0, 0, CardCategory.Command,
            faction: Faction.Japan, rarity: Rarity.Limited, tags: new[] { "空军" })),
        new CardDefinitionEntry(CntNavyId, new CardDefinition("反潜戒备", 1, 0, 0, 0, CardCategory.Counter,
            faction: Faction.Britain, rarity: Rarity.Special, tags: new[] { "海军" })),
    };

    /// <summary>创建元数据测试对局（双方卡组＝日本基础空军 x10；样本定义集；种子 42）。</summary>
    private static Match CreateMetadataMatch()
        => new(
            new CardList(Enumerable.Repeat(JpStdAirId, 10)),
            new CardList(Enumerable.Repeat(JpStdAirId, 10)),
            CreateDefinitions(),
            seed: 42,
            packageActivator: TestHost.Activator);

    /// <summary>实例化＋加载一张样本卡（归属＝player）。</summary>
    private static async Task<CardBase> InstantiateLoadedAsync(Match match, Player player, string id)
    {
        var card = match.CardLibrary.Instantiate(id);
        await card.LoadAsync(player);
        return card;
    }

    /// <summary>创建已加载夹具：初始化对局 ＋ 逐张加载全样本（场景①②④的测试内卡集合）。</summary>
    private static async Task<(Match Match, Player Player, IReadOnlyList<CardBase> Samples)> CreateLoadedFixtureAsync()
    {
        var match = CreateMetadataMatch();
        await match.Initialize();
        var player = match.Players[0];
        var samples = new List<CardBase>();
        foreach (var id in SampleIds)
        {
            samples.Add(await InstantiateLoadedAsync(match, player, id));
        }

        return (match, player, samples);
    }

    /// <summary>按名称取样本（名称唯一）。</summary>
    private static CardBase ByName(IReadOnlyList<CardBase> samples, string name)
        => Assert.Single(samples, card => card.Name == name);

    // ==========================================================
    // A. TagData 组件（必填强类型槽位 ＋ 开放 tag 增删查）
    // ==========================================================

    [Fact]
    public void TagData_Slots_Expose_Typed_Values_As_Required_And_ReadOnly()
    {
        var data = new TagData(Rarity.Elite);

        // 必填字段恒有值：稀有度可读（只读——无写面）
        // S10 随改：国籍槽位自 TagData 移出——国籍读取面迁至「阵营〔国籍〕＋部署费」合并组件
        // （FactionCostData；跨组件的国籍/花费读数与受控写面完整验证见 FactionCostComponentTests）。
        // 资产化 批次 4 随改：稀有度由枚举槽位改为 string（真源＝资产服务；TagSlot 仍为泛型枚举槽位机制）。
        Assert.Equal(Rarity.Elite, data.Rarity);

        // 开放 tag 初始为空（合法——开放维可零值；槽位不可零值）
        Assert.Empty(data.Tags);
    }

    [Fact]
    public void TagData_Open_Tags_Add_Remove_Contains_Are_Idempotent_And_Ordered()
    {
        var data = new TagData(Rarity.Standard);
        Assert.Empty(data.Tags);

        Assert.True(data.AddTag("海军"));  // 登记
        Assert.False(data.AddTag("海军")); // 幂等：重复登记＝false 无操作
        Assert.True(data.AddTag("T-34"));
        Assert.Equal(new[] { "海军", "T-34" }, data.Tags); // 登记序

        Assert.True(data.ContainsTag("海军"));
        Assert.False(data.ContainsTag("谢尔曼"));
        Assert.False(data.ContainsTag(null!)); // 存在性查询不抛错

        Assert.True(data.RemoveTag("海军"));
        Assert.False(data.RemoveTag("海军")); // 幂等：移除不存在＝false 无操作
        Assert.False(data.ContainsTag("海军"));
        Assert.Equal(new[] { "T-34" }, data.Tags);
    }

    [Fact]
    public void TagData_Rejects_Blank_Tags()
    {
        var data = new TagData(Rarity.Limited);
        Assert.Throws<ArgumentException>(() => data.AddTag(""));
        Assert.Throws<ArgumentException>(() => data.AddTag("  "));
        Assert.Throws<ArgumentNullException>(() => data.AddTag(null!));
        Assert.Throws<ArgumentException>(() => data.RemoveTag("  "));
        Assert.Throws<ArgumentNullException>(() => data.RemoveTag(null!));
    }


    // ==========================================================
    // B. 定义期声明（fail-fast）
    // ==========================================================

    [Fact]
    public void Definition_Requires_Faction_And_Rarity_Explicitly_FailFast()
    {
        // 缺失必填＝定义期 fail-fast 拒绝（无可用默认值——「省略即默认」不成立）
        var ex1 = Assert.Throws<ArgumentException>(() => new CardDefinition("缺国籍", 1, 1, 1, 1));
        Assert.Contains("未提供国籍", ex1.Message);

        var ex2 = Assert.Throws<ArgumentException>(() =>
            new CardDefinition("缺稀有度", 1, 1, 1, 1, faction: Faction.Germany));
        Assert.Contains("未提供稀有度", ex2.Message);
    }

    [Fact]
    public void Definition_Rejects_Undefined_Faction_And_Rarity_Values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CardDefinition(
            "怪国籍", 1, 1, 1, 1, faction: "未定义阵营", rarity: Rarity.Standard));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CardDefinition(
            "怪稀有度", 1, 1, 1, 1, faction: Faction.Germany, rarity: "未定义稀有度"));
    }

    [Fact]
    public void Definition_Rejects_Blank_And_Duplicate_Tags()
    {
        var ex1 = Assert.Throws<ArgumentException>(() => new CardDefinition(
            "空白tag", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard, tags: new[] { "海军", " " }));
        Assert.Contains("开放 tag", ex1.Message);

        Assert.Throws<ArgumentException>(() => new CardDefinition(
            "空元素tag", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard, tags: new[] { "海军", null! }));

        var ex2 = Assert.Throws<ArgumentException>(() => new CardDefinition(
            "重复tag", 1, 1, 1, 1, faction: Faction.Germany, rarity: Rarity.Standard, tags: new[] { "海军", "海军" }));
        Assert.Contains("重复项", ex2.Message);
    }

    [Fact]
    public void Definition_Accepts_And_Copies_Faction_Rarity_And_Tags()
    {
        // 缺省 tags＝空列表（合法——开放维可零值）；槽位终值可读
        var plain = new CardDefinition("无tag", 1, 1, 1, 1, faction: Faction.Neutral, rarity: Rarity.Standard);
        Assert.Equal(Faction.Neutral, plain.Faction);
        Assert.Equal(Rarity.Standard, plain.Rarity);
        Assert.Empty(plain.Tags);

        // 声明值拷贝与登记序保留
        var tagged = new CardDefinition("有tag", 1, 1, 1, 1,
            faction: Faction.Finland, rarity: Rarity.Special, tags: new[] { "海军", "谢尔曼" });
        Assert.Equal(Faction.Finland, tagged.Faction);
        Assert.Equal(Rarity.Special, tagged.Rarity);
        Assert.Equal(new[] { "海军", "谢尔曼" }, tagged.Tags);
    }

    // ==========================================================
    // C. 加载装配链（三类覆盖；先于 card.load 广播；未加载读取明确错误）
    // ==========================================================

    [Fact]
    public async Task TagData_Is_Attached_On_Load_For_All_Three_Categories_From_Definition()
    {
        var (match, player, _) = await CreateLoadedFixtureAsync();

        // 单位 / 指令 / 反制三类均装配（稀有度/开放 tag 经 TagData 可读；国籍经合并组件 FactionCostData 可读——
        // S10 重构：国籍自 TagData 移出、与部署费合并入同组件；全类别覆盖）
        var unit = await InstantiateLoadedAsync(match, player, JpEliteAirId);
        var command = await InstantiateLoadedAsync(match, player, CmdAirId);
        var counter = await InstantiateLoadedAsync(match, player, CntNavyId);

        var unitFactionCost = unit.GetData<FactionCostData>();
        Assert.Equal(Faction.Japan, unitFactionCost.Faction);
        var unitData = unit.GetData<TagData>();
        Assert.Equal(Rarity.Elite, unitData.Rarity);
        Assert.Equal(new[] { "空军", "海军" }, unitData.Tags);

        var commandFactionCost = command.GetData<FactionCostData>();
        Assert.Equal(Faction.Japan, commandFactionCost.Faction);
        var commandData = command.GetData<TagData>();
        Assert.Equal(Rarity.Limited, commandData.Rarity);
        Assert.True(commandData.ContainsTag("空军"));

        var counterFactionCost = counter.GetData<FactionCostData>();
        Assert.Equal(Faction.Britain, counterFactionCost.Faction);
        var counterData = counter.GetData<TagData>();
        Assert.Equal(Rarity.Special, counterData.Rarity);
        Assert.True(counterData.ContainsTag("海军"));
    }

    [Fact]
    public async Task TagData_Is_Ready_Before_CardLoad_Broadcast()
    {
        var match = CreateMetadataMatch();
        var reads = new List<bool>();

        // 订阅在 Initialize 前挂接：在每条 card.load 的处理窗口内读取元数据——须已就绪（登记先于广播）
        // （TagData＝加载时装配；FactionCostData＝构造期装配——两者均在 card.load 窗口内已就绪，S10 随改）
        using var subscription = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardLoad)
            {
                var card = (CardBase)payload![GameUpdates.PayloadCard]!;
                reads.Add(card.TryGetData<TagData>(out var data)
                    && card.TryGetData<FactionCostData>(out var factionCost)
                    && factionCost.Faction == Faction.Japan
                    && data.Rarity == Rarity.Standard
                    && data.ContainsTag("空军"));
            }

            return Task.CompletedTask;
        });

        await match.Initialize();

        // 初始化加载 20 张（双方卡组 10＋10）：每条广播窗口内均可读到已就绪的槽位与开放 tag（查询不到未就绪状态）
        Assert.Equal(20, reads.Count);
        Assert.All(reads, value => Assert.True(value));
    }

    [Fact]
    public async Task Unloaded_Or_Standalone_Card_Reading_TagData_Is_Explicit_Error()
    {
        var match = CreateMetadataMatch();
        await match.Initialize();

        // 已实例化、未走加载：组件缺失＝明确错误（不构造期挂空组件）
        var unloaded = match.CardLibrary.Instantiate(JpEliteAirId);
        Assert.Throws<KeyNotFoundException>(() => unloaded.GetData<TagData>());

        // 独立构造（脱离加载链）同样＝明确错误
        var standalone = new UnitCard(match.Engine, match.CardLibrary.Get(JpEliteAirId));
        Assert.Throws<KeyNotFoundException>(() => standalone.GetData<TagData>());
    }

    // ==========================================================
    // 场景①：组合筛选查询（国籍×稀有度×子类别，如「日本精英空军」式）
    // ==========================================================

    [Fact]
    public async Task Scenario1_Combined_Filter_Faction_Rarity_Subcategory()
    {
        var (_, _, samples) = await CreateLoadedFixtureAsync();

        // 「日本精英空军」：国籍×稀有度×子类别 三条件 AND
        // （S10 随改：国籍经合并组件 FactionCostData 读取；稀有度/tag 经 TagData——组合筛选范式跨组件组合）
        var result = samples.Where(card =>
        {
            var factionCost = card.GetData<FactionCostData>();
            var data = card.GetData<TagData>();
            return factionCost.Faction == Faction.Japan
                && data.Rarity == Rarity.Elite
                && data.ContainsTag("空军");
        }).ToList();

        Assert.Single(result);
        Assert.Same(ByName(samples, "日本精英空军"), result[0]);

        // 条件收放对照：日本×精英（去子类别）＝两枚；日本×精英×海军＝一枚（同集合多值维度见场景②）
        var japaneseElite = samples.Where(card =>
        {
            var factionCost = card.GetData<FactionCostData>();
            var data = card.GetData<TagData>();
            return factionCost.Faction == Faction.Japan && data.Rarity == Rarity.Elite;
        }).ToList();
        Assert.Equal(2, japaneseElite.Count);

        var japaneseEliteNavy = samples.Where(card =>
        {
            var factionCost = card.GetData<FactionCostData>();
            var data = card.GetData<TagData>();
            return factionCost.Faction == Faction.Japan && data.Rarity == Rarity.Elite && data.ContainsTag("海军");
        }).ToList();
        Assert.Single(japaneseEliteNavy);

        // 负例：法国×特殊＝空集（条件组合不误放行）
        Assert.DoesNotContain(samples, card =>
        {
            var factionCost = card.GetData<FactionCostData>();
            var data = card.GetData<TagData>();
            return factionCost.Faction == Faction.France && data.Rarity == Rarity.Special;
        });
    }

    // ==========================================================
    // 场景②：开放子类别查询（海军 / T-34；同集合多值 AND）
    // ==========================================================

    [Fact]
    public async Task Scenario2_Open_Subcategory_Filter_By_Single_Value_And_MultiValue_And()
    {
        var (_, _, samples) = await CreateLoadedFixtureAsync();

        // 单值：海军 → 四国五枚（跨 日本/苏联/美国/英国）
        var navy = samples.Where(card => card.GetData<TagData>().ContainsTag("海军")).ToList();
        Assert.Equal(5, navy.Count);
        Assert.Contains(ByName(samples, "日本精英空军"), navy);
        Assert.Contains(ByName(samples, "T-34坦克"), navy);
        Assert.Contains(ByName(samples, "谢尔曼"), navy);
        Assert.Contains(ByName(samples, "皇家海军"), navy);
        Assert.Contains(ByName(samples, "反潜戒备"), navy);
        Assert.DoesNotContain(ByName(samples, "法国白板"), navy); // 无 tag 卡不命中

        // 单值：T-34 → 一枚
        var t34 = samples.Where(card => card.GetData<TagData>().ContainsTag("T-34")).ToList();
        Assert.Single(t34);
        Assert.Same(ByName(samples, "T-34坦克"), t34[0]);

        // 同一开放集合内多值 AND：海军 AND T-34 → 一枚
        var navyAndT34 = samples.Where(card =>
        {
            var data = card.GetData<TagData>();
            return data.ContainsTag("海军") && data.ContainsTag("T-34");
        }).ToList();
        Assert.Single(navyAndT34);
        Assert.Same(ByName(samples, "T-34坦克"), navyAndT34[0]);

        // 单值：谢尔曼 → 一枚
        var sherman = samples.Where(card => card.GetData<TagData>().ContainsTag("谢尔曼")).ToList();
        Assert.Single(sherman);
        Assert.Same(ByName(samples, "谢尔曼"), sherman[0]);
    }

    // ==========================================================
    // 场景③：构筑外判定（初始化内 vs 构筑外生成牌模拟；未加载＝拒绝）
    // ==========================================================

    [Fact]
    public async Task Scenario3_Initialized_Cards_Are_Not_Outside_Deck()
    {
        var match = CreateMetadataMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 起手 4 张＝初始化加载的卡（ID ≤ 水位线）——判定＝否（双访问口：对局转发口 + 玩家管理器口）
        Assert.Equal(4, player.Hand.Count);
        for (var i = 0; i < player.Hand.Count; i++)
        {
            var card = (CardBase)player.Hand[i];
            Assert.False(match.IsOutsideDeck(card));
            Assert.False(match.PlayerManager.IsOutsideDeck(card));
        }
    }

    [Fact]
    public async Task Scenario3_Generated_Card_After_Init_Is_Outside_Deck()
    {
        var match = CreateMetadataMatch();
        await match.Initialize();
        var player = match.Players[0];

        // 构筑外生成牌模拟：经既有「实例化＋加载」路径——加载时获得 ＞水位线 的新 ID
        var generated1 = await InstantiateLoadedAsync(match, player, JpEliteAirId);
        var generated2 = await InstantiateLoadedAsync(match, player, JpEliteAirId);
        Assert.True(match.IsOutsideDeck(generated1));
        Assert.True(match.IsOutsideDeck(generated2));

        // 对照：初始化内实例（起手卡）保持「否」（不被后来者影响）
        Assert.False(match.IsOutsideDeck((CardBase)player.Hand[0]));
    }

    [Fact]
    public async Task Scenario3_Unloaded_Or_Standalone_Card_Judgement_Is_Rejected()
    {
        var match = CreateMetadataMatch();
        await match.Initialize();

        // 未加载卡（已实例化、未走加载）：未分配 ID＝明确失败（拒绝——不静默返回 false）
        var unloaded = match.CardLibrary.Instantiate(JpEliteAirId);
        Assert.Throws<InvalidOperationException>(() => match.IsOutsideDeck(unloaded));

        // 独立构造的卡经加载（无提供器＝不分配 ID）：同样明确拒绝
        var standalone = new UnitCard(match.Engine, match.CardLibrary.Get(JpEliteAirId));
        await standalone.LoadAsync(match.Players[0]);
        Assert.Throws<InvalidOperationException>(() => match.IsOutsideDeck(standalone));
    }

    [Fact]
    public async Task Scenario3_Repeated_Load_Keeps_Same_Id_And_Judgement_Not_Distorted()
    {
        var match = CreateMetadataMatch();
        await match.Initialize();
        var player = match.Players[0];
        var card = (CardBase)player.Hand[0]; // 初始化内卡
        Assert.False(match.PlayerManager.IsOutsideDeck(card));

        // 重复加载：装配链拒绝重复装配（TagData 已装配——AddData 契约「每类型恰一份」，与词条先例同精神）；
        // ID 保持原值（同一实例同一 ID——未被重新分配为 ＞水位线 的新 ID）
        await Assert.ThrowsAsync<InvalidOperationException>(() => card.LoadAsync(player));
        Assert.False(match.PlayerManager.IsOutsideDeck(card));
    }

    // ==========================================================
    // 场景④：开放 tag 运行时增删生效（同实例即时读回＋筛选随动）
    // ==========================================================

    [Fact]
    public async Task Scenario4_Runtime_Tag_Add_And_Remove_Affect_Readback_And_Filtering()
    {
        var (match, player, samples) = await CreateLoadedFixtureAsync();
        var card = ByName(samples, "法国白板"); // 无开放 tag
        var data = card.GetData<TagData>();
        Assert.Empty(data.Tags);

        // 增：同实例即时读回
        Assert.True(data.AddTag("海军"));
        Assert.True(data.ContainsTag("海军"));
        Assert.Equal(new[] { "海军" }, data.Tags);

        // 筛选随动：增后即查即得
        var navyAfterAdd = samples.Where(c => c.GetData<TagData>().ContainsTag("海军")).ToList();
        Assert.Equal(6, navyAfterAdd.Count);
        Assert.Contains(card, navyAfterAdd);

        // 删：同实例即时读回
        Assert.True(data.RemoveTag("海军"));
        Assert.False(data.ContainsTag("海军"));
        Assert.Empty(data.Tags);

        // 筛选随动：删后即查即失
        var navyAfterRemove = samples.Where(c => c.GetData<TagData>().ContainsTag("海军")).ToList();
        Assert.Equal(5, navyAfterRemove.Count);
        Assert.DoesNotContain(card, navyAfterRemove);

        // 作用域：「定义静态、实例可变」——运行时可操作仅及目标实例；定义与同定义的其它实例不受影响
        Assert.Empty(match.CardLibrary.Get(FrPlainId).Tags);
        var another = await InstantiateLoadedAsync(match, player, FrPlainId);
        Assert.False(another.GetData<TagData>().ContainsTag("海军"));
        Assert.NotSame(data, another.GetData<TagData>());
    }
}
