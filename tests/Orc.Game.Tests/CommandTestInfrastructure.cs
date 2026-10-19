using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Orc.Game.Tests;

/// <summary>
/// 指挥/战斗/词条测试套件数据与工具（2C）：
/// 定制定义集（五类单位＋守护者＋零类型＋四词条＋费用例外＋巨炮）覆盖矩阵/结算/词条场景；
/// 对局构造（可选桥接）、单位准备（加载＋加入路径上场——不扣费）、“激活”置位、全引用候选脚本、命令驱动助手。
/// </summary>
internal static class CommandTestKit
{
    // ---------- 定义 id（与 CreateDefinitions 一一对应） ----------

    /// <summary>步兵（步/坦仅相邻基线；攻 2 / 防 5）。</summary>
    public const string InfantryId = "u_inf";

    /// <summary>坦克（双动；攻 3 / 防 5）。</summary>
    public const string TankId = "u_tank";

    /// <summary>炮兵（任意线；攻 2 / 防 2）。</summary>
    public const string ArtilleryId = "u_art";

    /// <summary>战斗机（任意线；攻 3 / 防 2）。</summary>
    public const string FighterId = "u_fgt";

    /// <summary>轰炸机（任意线；攻 4 / 防 2）。</summary>
    public const string BomberId = "u_bmb";

    /// <summary>守护兵（守护者；攻 0 / 防 6）。</summary>
    public const string GuardianId = "u_guard";

    /// <summary>白板（零类型——视同步兵基线；攻 2 / 防 4）。</summary>
    public const string TypelessId = "u_none";

    /// <summary>闪击兵（步；攻 2 / 防 3）。</summary>
    public const string BlitzId = "u_blitz";

    /// <summary>奋战兵（步；攻 2 / 防 6）。</summary>
    public const string FuryId = "u_fury";

    /// <summary>烟幕兵（步；攻 2 / 防 4）。</summary>
    public const string SmokeId = "u_smoke";

    /// <summary>伏击兵（步；攻 5 / 防 6——伏击条件用高攻）。</summary>
    public const string AmbushId = "u_ambush";

    /// <summary>脆皮（步；攻 1 / 防 2）。</summary>
    public const string WeakId = "u_weak";

    /// <summary>巨兽（步；攻 6 / 防 7）。</summary>
    public const string BeastId = "u_beast";

    /// <summary>重费兵（行动费 2——费用不足场景）。</summary>
    public const string CostlyId = "u_cost2";

    /// <summary>巨炮（炮兵；攻 30 / 防 30——HQ 钳制场景）。</summary>
    public const string MegaId = "u_mega";

    /// <summary>混合体（坦克＋炮兵——多类型存在性判定：范围任意＋双动各规则独立成立）。</summary>
    public const string MixId = "u_mix";

    /// <summary>混成空军（炮兵＋轰炸机——多类型豁免判定：炮兵绝对豁免优先于轰炸机例外）。</summary>
    public const string MixAirId = "u_mixair";

    /// <summary>伏击战斗机（战斗机＋伏击——轰炸机例外的反击资格＋伏击改写场景）。</summary>
    public const string AmbushFighterId = "u_ambfgt";

    /// <summary>伏击轰炸机（轰炸机＋伏击——目标轰炸机永不反击＋改写不成立场景）。</summary>
    public const string AmbushBomberId = "u_ambbmb";

    /// <summary>轻指令（部署费 1——打出链终局门禁用例）。</summary>
    public const string OrderCardId = "u_cmd";

    /// <summary>轻反制（部署费 1——反制终局门禁用例）。</summary>
    public const string CounterCardId = "u_cnt";

    /// <summary>奋击坦克（坦克＋奋战——「移动一次＋攻击两次」叠加场景）。</summary>
    public const string FuryTankId = "u_furytank";

    /// <summary>定制定义集（22 枚；数值入测试断言对照。后置项补全批新增 5 枚：混成空军 / 伏击战斗机 / 伏击轰炸机 / 轻指令 / 轻反制）。
    /// W1-1 随改：必填槽位（国籍/稀有度）统一补 Germany / Standard（无特定语义卡取合理值）。</summary>
    public static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(InfantryId, new CardDefinition("步兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(TankId, new CardDefinition("坦克", 1, 1, 3, 5, unitTypes: new[] { UnitType.Tank }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(ArtilleryId, new CardDefinition("炮兵", 1, 1, 2, 2, unitTypes: new[] { UnitType.Artillery }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(FighterId, new CardDefinition("战斗机", 1, 1, 3, 2, unitTypes: new[] { UnitType.Fighter }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(BomberId, new CardDefinition("轰炸机", 1, 1, 4, 2, unitTypes: new[] { UnitType.Bomber }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(GuardianId, new CardDefinition("守护兵", 1, 1, 0, 6, unitTypes: new[] { UnitType.Infantry }, isGuard: true, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(TypelessId, new CardDefinition("白板", 1, 1, 2, 4, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(BlitzId, new CardDefinition("闪击兵", 1, 1, 2, 3, unitTypes: new[] { UnitType.Infantry }, keywords: new[] { new KeywordDeclaration(KeywordIds.Blitz) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(FuryId, new CardDefinition("奋战兵", 1, 1, 2, 6, unitTypes: new[] { UnitType.Infantry }, keywords: new[] { new KeywordDeclaration(KeywordIds.Fury) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(SmokeId, new CardDefinition("烟幕兵", 1, 1, 2, 4, unitTypes: new[] { UnitType.Infantry }, keywords: new[] { new KeywordDeclaration(KeywordIds.SmokeScreen) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(AmbushId, new CardDefinition("伏击兵", 1, 1, 5, 6, unitTypes: new[] { UnitType.Infantry }, keywords: new[] { new KeywordDeclaration(KeywordIds.Ambush) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(WeakId, new CardDefinition("脆皮", 1, 1, 1, 2, unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(BeastId, new CardDefinition("巨兽", 1, 1, 6, 7, unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(CostlyId, new CardDefinition("重费兵", 1, 2, 2, 5, unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(MegaId, new CardDefinition("巨炮", 1, 1, 30, 30, unitTypes: new[] { UnitType.Artillery }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(MixId, new CardDefinition("混合体", 1, 1, 3, 4, unitTypes: new[] { UnitType.Tank, UnitType.Artillery }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(MixAirId, new CardDefinition("混成空军", 1, 1, 2, 5, unitTypes: new[] { UnitType.Artillery, UnitType.Bomber }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(AmbushFighterId, new CardDefinition("伏击战斗机", 1, 1, 5, 2, unitTypes: new[] { UnitType.Fighter }, keywords: new[] { new KeywordDeclaration(KeywordIds.Ambush) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(AmbushBomberId, new CardDefinition("伏击轰炸机", 1, 1, 4, 2, unitTypes: new[] { UnitType.Bomber }, keywords: new[] { new KeywordDeclaration(KeywordIds.Ambush) }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(OrderCardId, new CardDefinition("轻指令", 1, 0, 0, 0, CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(CounterCardId, new CardDefinition("轻反制", 1, 0, 0, 0, CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(FuryTankId, new CardDefinition("奋击坦克", 1, 1, 2, 5, unitTypes: new[] { UnitType.Tank }, keywords: new[] { new KeywordDeclaration(KeywordIds.Fury) }, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    /// <summary>创建指挥测试对局（双方步兵 x10 卡组＋定制定义集；可选目标选择桥接）。
    /// W2c X2 加性：可选效果注册表（卡牌加载时效果装载的装配源）与追加定义集（测试专用卡；
    /// 与既有 id 重复的条目将导致初始化注册期 fail-fast——测试自行避免）。
    /// S6 随改：A4 部署逻辑路径已废弃（部署效果经「部署词条触发器 + 效果注入」表达）。</summary>
    public static Match CreateCommandMatch(
        MockTargeterBridge? bridge = null,
        int seed = 42,
        CardEffectRegistry? effectRegistry = null,
        IEnumerable<CardDefinitionEntry>? extraDefinitions = null,
        bool skipMulligan = true)
        => new(
            new CardList(Enumerable.Repeat(InfantryId, 10)),
            new CardList(Enumerable.Repeat(InfantryId, 10)),
            CreateDefinitions().Concat(extraDefinitions ?? Enumerable.Empty<CardDefinitionEntry>()),
            seed,
            firstPlayerIndex: null,
            options: new MatchOptions { SkipMulligan = skipMulligan },
            targeterBridge: bridge,
            effectRegistry: effectRegistry,
            packageActivator: TestHost.Activator);

    /// <summary>实例化＋加载一张定制卡（归属＝player；可选放入手牌）。</summary>
    public static async Task<UnitCard> InstantiateLoadedAsync(Match match, Player player, string id = InfantryId, bool toHand = false)
    {
        var card = (UnitCard)match.CardLibrary.Instantiate(id);
        await card.LoadAsync(player);
        if (toHand)
        {
            player.Hand.Add(card);
        }

        return card;
    }

    /// <summary>准备单位（加载＋加入路径上场到指定槽——不扣费、不走部署词条）；失败＝异常（测试装配错误）。</summary>
    public static async Task<UnitCard> PrepareUnitAsync(Match match, Player player, string id, Slot target)
    {
        var unit = await InstantiateLoadedAsync(match, player, id);
        var result = await match.PlayManager.JoinUnitAsync(unit, target);
        if (result.Status != PlayResultStatus.Success)
        {
            throw new InvalidOperationException($"准备单位失败：{result.FailureReason}");
        }

        return unit;
    }

    /// <summary>
    /// 准备单位到我方支援线的第 <paramref name="index"/> 个**可部署槽**（0 起、升序、自动跳过 HQ 占位槽）。
    /// 不按"线路第几格"取位：HQ 的占位槽由棋盘决定（<see cref="Battlefield.HqSlotIndex"/>，居中），
    /// 按绝对下标取位会在 HQ 挪动后撞上占位槽（TargetSlotOccupied）；按可部署序取位则与 HQ 位置无关。
    /// 注意 HQ 两侧都是邻位，因此 index 1 仍是紧邻 HQ 的那一格。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">可部署槽不足 index＋1 个。</exception>
    public static Task<UnitCard> PrepareOnSupportAsync(Match match, Player player, string id, int index)
    {
        var line = match.Battlefield.GetSupportLine(player);
        var deployable = Enumerable.Range(0, line.Count).Where(i => line[i].IsEmpty).ToArray();
        if (index < 0 || index >= deployable.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "可部署支援槽不足。");
        }

        return PrepareUnitAsync(match, player, id, line[deployable[index]]);
    }

    /// <summary>准备单位到前线指定槽（归属＝player——前线共享）。</summary>
    public static Task<UnitCard> PrepareOnFrontAsync(Match match, Player player, string id, int index)
        => PrepareUnitAsync(match, player, id, match.Battlefield.FrontLine[index]);

    /// <summary>“激活”单位（置两 bool——模拟本回合可行动状态；供不经回合恢复的用例）。</summary>
    public static void Activate(UnitCard unit, bool canMove = true, bool canAttack = true)
    {
        KeywordRules.ResetActionQuotas(unit);
        var command = unit.GetData<CommandData>();
        if (!canMove)
        {
            command.SetMoveQuota(0);
        }

        if (!canAttack)
        {
            command.SetAttackQuota(0);
        }
    }

    // ---------- 候选脚本（前端提交超集：全槽位＋全单位引用） ----------

    /// <summary>候选收集脚本（提交全战场槽位引用＋全在场单位引用——超集；后端粗筛收敛到候选面）。</summary>
    public static Func<object, Task<IReadOnlyList<object?>>> AllRefsScript(Match match)
        => _ => Task.FromResult<IReadOnlyList<object?>>(AllRefsOf(match));

    /// <summary>全引用（槽位引用＋单位引用＋双方 HQ 实体引用）。
    /// W3-3：HQ 目标以实体引用承载——收集超集须含 HQ 引用（与候选产出面同源；槽位引用不再作为 HQ 目标产出）。</summary>
    public static IReadOnlyList<object?> AllRefsOf(Match match)
    {
        var refs = new List<object?>();
        foreach (var slot in AllSlotsOf(match))
        {
            refs.Add(slot.Ref);
        }

        foreach (var unit in AllUnitsOf(match))
        {
            refs.Add(unit.Ref);
        }

        foreach (var player in match.Players)
        {
            refs.Add(player.Hq.Ref);
        }

        return refs;
    }

    /// <summary>全战场槽位（三线顺序枚举）。</summary>
    public static IEnumerable<Slot> AllSlotsOf(Match match)
        => match.Battlefield.PlayerASupportLine
            .Concat(match.Battlefield.FrontLine)
            .Concat(match.Battlefield.PlayerBSupportLine);

    /// <summary>全在场单位（槽位占用者枚举）。</summary>
    public static IEnumerable<UnitCard> AllUnitsOf(Match match)
        => AllSlotsOf(match).Select(slot => slot.Occupant).OfType<UnitCard>();

    // ---------- 命令驱动助手 ----------

    /// <summary>驱动一次指挥：发起 → 等待交互 → 选中目标完成 → 返回结果。</summary>
    public static async Task<CommandResult> RunCommandAsync(
        Match match, MockTargeterBridge bridge, UnitCard unit, Ref<Entity> targetRef)
    {
        var task = match.CommandManager.BeginCommandAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        var accepted = responder.Complete(
            description.RequestId, TargeterTestKit.Selection(TargetSlot.DefaultName, targetRef));
        if (!accepted)
        {
            throw new InvalidOperationException("驱动指挥失败：交互完成被拒绝（目标不在候选面）。");
        }

        return await task;
    }

    /// <summary>驱动一次指挥后取消（拖回）：发起 → 等待交互 → 取消 → 返回结果。</summary>
    public static async Task<CommandResult> CancelCommandAsync(Match match, MockTargeterBridge bridge, UnitCard unit)
    {
        var task = match.CommandManager.BeginCommandAsync(unit);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        var accepted = responder.Cancel(description.RequestId);
        if (!accepted)
        {
            throw new InvalidOperationException("驱动取消失败：取消被拒绝。");
        }

        return await task;
    }
}
