using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S9（G6+G13 生成·复制·转换）测试数据与工具：
/// ①专用定义集（发射源〔效果宿主〕、轻步兵〔衍生卡/生成目标/转换目标〕、精锐兵〔转换目标〕、钳击兵〔离场通知验证〕）；
/// ②场景效果桩（效果内经上下文取用面 <see cref="MatchCardService.ResolveFor"/>〔「卡 → 玩家 → 服务」〕真实取用服务
///   并完成场景操作——「效果端到端 / 等价上下文取用路径」）；③更新记录器；④场景对局构造与准备助手。
/// </summary>
internal static class S9Kit
{
    /// <summary>发射源（效果宿主——指令卡；不参与战斗）。</summary>
    public const string EmitterId = "u_s9_emit";

    /// <summary>轻步兵（生成目标/衍生卡——「构筑外」验证用；攻 1 / 防 2）。</summary>
    public const string LightInfantryId = "u_s9_light";

    /// <summary>精锐兵（转换目标——定义 X；坦克类型；攻 3 / 防 4）。</summary>
    public const string EliteId = "u_s9_elite";

    /// <summary>钳击兵（离场通知接入验证；攻 2 / 防 3）。</summary>
    public const string PincerUnitId = "u_s9_pincer";

    /// <summary>场景效果标识（声明到发射源）。</summary>
    public const string ProbeEffectId = "effect.s9.probe";

    /// <summary>专用定义集（4 枚；装配期注册进卡库——「衍生卡定义随装配期注册」）。</summary>
    public static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(EmitterId, new CardDefinition(
            "发射源", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(LightInfantryId, new CardDefinition(
            "轻步兵", deployCost: 1, operateCost: 1, attack: 1, defense: 2,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(EliteId, new CardDefinition(
            "精锐兵", deployCost: 2, operateCost: 1, attack: 3, defense: 4,
            unitTypes: new[] { UnitType.Tank }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(PincerUnitId, new CardDefinition(
            "钳击兵", deployCost: 1, operateCost: 1, attack: 2, defense: 3,
            unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Pincer) },
            faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    /// <summary>创建场景对局（双方步兵 x10 卡组＋S9 专用定义集；可选桥接/效果注册表）。</summary>
    public static Match CreateSceneMatch(
        MockTargeterBridge? bridge = null,
        CardEffectRegistry? effectRegistry = null,
        int seed = 42)
        => CommandTestKit.CreateCommandMatch(
            bridge, seed, effectRegistry, extraDefinitions: CreateDefinitions());

    /// <summary>实例化发射源＋装载（含效果桩），返回（卡，效果桩）；测试装配引用（Engine / CommandManager）随配置。</summary>
    public static async Task<(OrderCard Card, CardServiceSceneEffect Effect)> PrepareEmitterAsync(
        Match match, Player owner)
    {
        var card = (OrderCard)match.CardLibrary.Instantiate(EmitterId);
        await card.LoadAsync(owner);
        var effect = Assert.IsType<CardServiceSceneEffect>(Assert.Single(card.Effects));
        effect.Engine = match.Engine;
        effect.CommandManager = match.CommandManager;
        return (card, effect);
    }

    /// <summary>攻击力有效值（测试断言辅助）。</summary>
    public static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    /// <summary>攻击力有效值（测试断言辅助）。</summary>
    public static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    /// <summary>指定玩家的存活在场单位计数（测试断言辅助——「场上计数一致」）。</summary>
    public static int CountAliveUnitsOf(Match match, Player player)
        => CommandTestKit.AllUnitsOf(match)
            .Count(unit => ReferenceEquals(unit.Owner, player)
                && !unit.GetData<UnitStateData>().IsDestroyed);
}

/// <summary>场景施放视图（本批效果不消费施放载荷——[Optional] 锚点满足视图模板）。</summary>
[ContextView]
public class CardServiceCastView
{
    [Optional]
    [Read]
    public virtual object? Anchor { get; set; }
}

/// <summary>
/// 场景效果桩（S9；经真实效果执行链驱动——注册→装载→施放）：
/// 施放时经上下文取用面（<see cref="MatchCardService.ResolveFor"/>——「卡 → 玩家 → 服务」）真实取用服务并完成场景操作；
/// 「离场/销毁」段（转换组合）经测试装配引用（<see cref="Engine"/> / <see cref="CommandManager"/>）调用公共面
/// （离场受控入口＋销毁面——不得绕过公共面）；
/// 脱局（不可解析）＝功能不可用、不抛错、不失败（沿用既有先例）；异常＝记录到 <see cref="LastError"/>（可断言面）。
/// </summary>
internal sealed class CardServiceSceneEffect : ActiveEffect<CardServiceCastView>
{
    internal enum SceneOp
    {
        ToHand,           // ① 生成 → 手牌
        ToSupportLine,    // ② 生成 → 阵线（指定槽）
        ToAdjacent,       // ③ 生成 → 相邻处（解析＋选取＋落位）
        ToDeckTop,        // ④ 生成 → 卡组顶
        IntoDeckShuffled, // ④ 生成 → 洗入
        CopyToHand,       // ⑤ 复制（定义级）→ 手牌
        Transform,        // ⑥ 转换（组合链：就绪 → 离场 → 销毁 → 放回原位置）
        AddUnitType,      // ⑦ 类型增补（效果内调用受控入口）
    }

    public CardServiceSceneEffect(SceneOp op, string definitionId)
        : base("卡牌服务场景桩")
    {
        Op = op;
        DefinitionId = definitionId;
        CastTrigger.Register("施放", OnCastAsync);
    }

    public SceneOp Op { get; }

    public string DefinitionId { get; }

    // ---------- 场景参数（测试装配——Initialize 后配置） ----------

    /// <summary>对局引擎（测试装配引用——「离场/销毁」段调用公共面）。</summary>
    public LogicEngine? Engine { get; set; }

    /// <summary>指挥管理器（测试装配引用——离场受控入口）。</summary>
    public CommandManager? CommandManager { get; set; }

    /// <summary>目标槽位（ToSupportLine）。</summary>
    public FormationGap? TargetSlot { get; set; }

    /// <summary>源卡（CopyToHand / Transform / AddUnitType）。</summary>
    public CardBase? Source { get; set; }

    /// <summary>增补类型（AddUnitType）。</summary>
    public string AddedType { get; set; } = UnitType.Tank;

    /// <summary>选取策略（ToAdjacent；缺省＝取首个候选——策略由调用方定）。</summary>
    public Func<IReadOnlyList<FormationGap>, FormationGap?> SlotSelector { get; set; } = slots => slots[0];

    // ---------- 观测面 ----------

    /// <summary>上下文取用面已解析成功（服务可达——端到端取用通路的断言点）。</summary>
    public bool ServiceResolved { get; private set; }

    /// <summary>最近一次操作结果（未执行＝null）。</summary>
    public CardPlaceResult? LastResult { get; private set; }

    /// <summary>最近一次相邻解析的候选集（回调未到达＝null——空解析时选取不发生）。</summary>
    public IReadOnlyList<FormationGap>? LastCandidates { get; private set; }

    /// <summary>最近一次异常（异常隔离前提下的可断言面；无＝null）。</summary>
    public Exception? LastError { get; private set; }

    private async Task OnCastAsync(CardServiceCastView view, Context ctx, CancellationToken ct)
    {
        try
        {
            var service = MatchCardService.ResolveFor(Host); // ← 上下文取用面（卡 → 玩家 → 服务）
            if (service is null)
            {
                return; // 脱局降级：功能不可用、不抛错、不失败
            }

            ServiceResolved = true;

            if (Host is not CardBase host || host.Owner is not { } owner)
            {
                return;
            }

            switch (Op)
            {
                case SceneOp.ToHand:
                    LastResult = await service.CreateAndPlaceToHandAsync(DefinitionId, owner, ct);
                    break;

                case SceneOp.ToSupportLine:
                {
                    if (TargetSlot is null)
                    {
                        return;
                    }

                    LastResult = await service.CreateAndPlaceToSupportLineAsync(DefinitionId, owner, TargetSlot, ct);
                    break;
                }

                case SceneOp.ToAdjacent:
                    LastResult = await service.CreateAndPlaceToAdjacentAsync(
                        DefinitionId,
                        owner,
                        slots =>
                        {
                            LastCandidates = slots.ToArray();
                            return SlotSelector(slots);
                        },
                        ct);
                    break;

                case SceneOp.ToDeckTop:
                    LastResult = await service.CreateAndPlaceToDeckTopAsync(DefinitionId, owner, ct);
                    break;

                case SceneOp.IntoDeckShuffled:
                    LastResult = await service.CreateAndPlaceIntoDeckShuffledAsync(DefinitionId, owner, ct);
                    break;

                case SceneOp.CopyToHand:
                {
                    if (Source is null)
                    {
                        return;
                    }

                    if (!service.TryResolveDefinitionId(Source, out var copyId))
                    {
                        return; // 不可读（未注册）：不复制
                    }

                    LastResult = await service.CreateAndPlaceToHandAsync(copyId, owner, ct);
                    break;
                }

                case SceneOp.Transform:
                {
                    if (Source is not UnitCard source
                        || !source.TryGetData<UnitStateData>(out var sourceState)
                        || sourceState.Line is not { } slot)
                    {
                        return;
                    }

                    // 原位置序号（离场前取得：元素移出后右侧元素左移一位，因此「原位」＝按原序号插入）。
                    var originalOrdinal = slot.IndexOf(source);

                    // 组合链（转换＝组合形态；目标定义＝DefinitionId〔直接指定的定义 X〕；顺序＝「先判定/先就绪后替换」——
                    // 失败不损坏原实例状态）：
                    // ① 先就绪（按目标定义创建静态新实例——失败＝原实例保留、不发生任何替换）
                    var newCard = (UnitCard)await service.CreateAsync(DefinitionId, owner, ct);
                    // ② 离场（位置释放＋离场失效通知——公共受控入口）
                    await CommandManager!.LeaveBattlefieldAsync(source, ct);
                    // ③ 销毁（杀＋card.destroyed＋资源清理——配套销毁面）
                    await Engine!.DestroyCard(source);
                    // ④ 放回原位置（统一放置面——按原序号取当前插入点）
                    var gaps = slot.GetInsertionGaps();
                    var restoreOrdinal = originalOrdinal >= 0 && originalOrdinal < gaps.Count ? originalOrdinal : gaps.Count - 1;
                    LastResult = await service.PlaceToSupportLineAsync(newCard, owner, gaps[restoreOrdinal], ct);
                    break;
                }

                case SceneOp.AddUnitType:
                {
                    if (Source is not UnitCard typeTarget)
                    {
                        return;
                    }

                    await typeTarget.AddUnitTypeAsync(AddedType, ct); // 单位数据面受控入口（⑦不进服务面）
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex; // 记录（异常隔离前提下的可断言面）
        }
    }
}

/// <summary>
/// 更新记录器扩展（S9 测试辅助；复用既有 <see cref="UpdateRecorder"/>〔TestInfrastructure〕——
/// 补充按类型计数/取载荷/总数三个只读断言辅助，不动既有类）。
/// </summary>
internal static class UpdateRecorderExtensions
{
    /// <summary>指定类型的记录数。</summary>
    public static int CountOf(this UpdateRecorder recorder, string type)
        => recorder.Types.Count(item => string.Equals(item, type, StringComparison.Ordinal));

    /// <summary>总记录数。</summary>
    public static int TotalCount(this UpdateRecorder recorder) => recorder.Updates.Count;

    /// <summary>指定类型的首条载荷（无＝null）。</summary>
    public static IReadOnlyDictionary<string, object?>? PayloadOf(this UpdateRecorder recorder, string type)
        => recorder.Updates
            .FirstOrDefault(item => string.Equals(item.Type, type, StringComparison.Ordinal))
            .Payload;
}
