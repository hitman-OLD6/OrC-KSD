using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// C2（开发/发现完整链）测试数据与工具：
/// ①专用定义集（发射源〔效果宿主〕＋取样池用卡；随装配期注册进卡库——取样池＝本局卡库全体）；
/// ②完整链效果（<see cref="C2DevelopEffect"/>）：效果内经执行上下文取用面（「卡 → 玩家 → 服务」）
///   真实取用〔对局随机服务 / 对局卡牌服务 / 目标选择管理器〕，完成「取池（定义级名单）→ 卡牌选择器 N 选一
///   → 选中项生成实例（S9 创建）→ 落位（S9 放置；手牌/卡组顶）」端到端链；
/// ③场景对局构造与准备助手。
/// 示范范式说明：本文件为测试内组合范式（供未来效果方参照/复制）；不新增生产示范件。
/// </summary>
internal static class C2Kit
{
    /// <summary>发射源（效果宿主——指令卡；不参与战斗）。</summary>
    public const string EmitterId = "u_c2_emit";

    /// <summary>完整链效果标识（声明到发射源）。</summary>
    public const string DevelopEffectId = "effect.c2.develop";

    /// <summary>取样池专用卡（参与池；供枚举序/锚点差异锚定）。</summary>
    public const string PoolCard1Id = "u_c2_pool1";

    /// <summary>取样池专用卡（参与池）。</summary>
    public const string PoolCard2Id = "u_c2_pool2";

    /// <summary>取样池专用卡（参与池）。</summary>
    public const string PoolCard3Id = "u_c2_pool3";

    /// <summary>默认链路槽位名（卡牌选择器槽位）。</summary>
    public const string SlotName = "pick";

    /// <summary>专用定义集（4 枚：发射源＋三枚池卡；装配期注册进卡库）。</summary>
    public static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(EmitterId, new CardDefinition(
            "开发源", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(PoolCard1Id, new CardDefinition(
            "侦察兵", deployCost: 1, operateCost: 1, attack: 1, defense: 2,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(PoolCard2Id, new CardDefinition(
            "装甲侦察", deployCost: 2, operateCost: 1, attack: 2, defense: 3,
            unitTypes: new[] { UnitType.Tank }, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(PoolCard3Id, new CardDefinition(
            "火力支援", deployCost: 2, operateCost: 1, attack: 2, defense: 2,
            unitTypes: new[] { UnitType.Artillery }, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    /// <summary>创建场景对局（双方步兵 x10 卡组＋定制定义集＋C2 专用定义集；桥接/效果注册表/种子可传）。</summary>
    public static Match CreateSceneMatch(MockTargeterBridge bridge, CardEffectRegistry registry, int seed)
        => CommandTestKit.CreateCommandMatch(bridge, seed, registry, extraDefinitions: CreateDefinitions());

    /// <summary>构造完整链效果注册表（工厂＋声明——效果装配面）。</summary>
    public static CardEffectRegistry CreateRegistry(
        int drawCount, C2Destination destination, Func<string, bool>? poolFilter = null)
    {
        var registry = new CardEffectRegistry();
        registry.Register(DevelopEffectId, _ => new C2DevelopEffect(drawCount, destination, poolFilter));
        registry.Declare(EmitterId, new[] { DevelopEffectId });
        return registry;
    }

    /// <summary>实例化发射源＋装载（含效果装载链），返回（卡，效果）；测试装配引用随配。</summary>
    public static async Task<(OrderCard Card, C2DevelopEffect Effect)> PrepareEmitterAsync(Match match, Player owner)
    {
        var card = (OrderCard)match.CardLibrary.Instantiate(EmitterId);
        await card.LoadAsync(owner);
        var effect = Assert.IsType<C2DevelopEffect>(Assert.Single(card.Effects));
        return (card, effect);
    }

    /// <summary>固定选样（确定性选择——不消费随机）：取取样名单末张（名单非空时任一固定规则等价）。</summary>
    public static string PickFixed(IReadOnlyList<string> drawnIds) => drawnIds[^1];
}

/// <summary>落位去向（C2 链的显式调用点／参数——不写死手牌；每个去向独立场景、独立组装）。</summary>
internal enum C2Destination
{
    /// <summary>置入手牌（S9 统一放置服务——手牌去向：尾部追加＋card.hand.add）。</summary>
    Hand,

    /// <summary>置于卡组顶（S9 统一放置服务——卡组顶去向：装入取件端、静默）。</summary>
    DeckTop,
}

/// <summary>完整链施放视图（本效果不消费施放载荷——[Optional] 锚点满足视图模板）。</summary>
[ContextView]
public class C2CastView
{
    [Optional]
    [Read]
    public virtual object? Anchor { get; set; }
}

/// <summary>
/// 开发/发现完整链效果（C2；经真实效果执行链驱动——注册 → 装载 → 施放）：
/// 施放时经上下文取用面（「卡 → 玩家 → 服务」）真实取用三个服务〔<see cref="MatchRandomService.ResolveFor"/> /
/// <see cref="MatchCardService.ResolveFor"/> / <see cref="TargeterManager.ResolveFor"/>〕并完成全链：
/// ① 卡池＝本局卡牌库已注册定义的全体（经枚举读面）＋可选效果侧筛选（自备候选集）→ 规范化排序；
/// ② 先判池：池空（M=0）＝链级终止（不构造选择器、无 targeting 请求、零副作用、不抛）；
/// ③ 取样：经对局随机服务 <c>PickN</c>（不放回；n＝min(N, 池数)）；
/// ④ 构造卡牌选择器（名单形态——定义级标识＋可读名称）→ 经目标选择管理器出题（桥接交互）；
/// ⑤ 终局：取消/失败＝链终止（零副作用）；成功＝按槽位名读出选中标识；
/// ⑥ 生成实例并落位（S9 统一放置服务——去向为链参数：手牌/卡组顶）。
/// 禁旁路口径：服务获取与调用均在运行期由效果自身完成（测试仅装配数据＋驱动桥接＋断言）；
/// 脱局（不可解析）＝功能不可用、不抛错、不失败（沿用既有先例）；异常＝记录到 <see cref="LastError"/>（可断言面）。
/// </summary>
internal sealed class C2DevelopEffect : ActiveEffect<C2CastView>
{
    private readonly int _drawCount;
    private readonly C2Destination _destination;
    private readonly Func<string, bool>? _poolFilter;

    public C2DevelopEffect(int drawCount, C2Destination destination, Func<string, bool>? poolFilter = null)
        : base("开发发现完整链")
    {
        _drawCount = drawCount;
        _destination = destination;
        _poolFilter = poolFilter;
        CastTrigger.Register("施放", OnCastAsync);
    }

    // ---------- 观测面（测试断言用） ----------

    /// <summary>执行次数（取消后隔离场景＝两段执行）。</summary>
    public int RunCount { get; private set; }

    /// <summary>三个上下文取用面均解析成功（服务可达——端到端取用通路的断言点）。</summary>
    public bool ServicesResolved { get; private set; }

    /// <summary>本轮取样池（经枚举读面＋筛选＋规范化排序后的实际池——断言「卡池＝库全体」用）。</summary>
    public IReadOnlyList<string> LastPool { get; private set; } = Array.Empty<string>();

    /// <summary>本轮取样名单（取样结果——不放回、互异）。</summary>
    public IReadOnlyList<string> LastDrawnIds { get; private set; } = Array.Empty<string>();

    /// <summary>池空终止分支命中（M=0——链级终止可观察）。</summary>
    public bool PoolEmptyTerminated { get; private set; }

    /// <summary>本轮 targeting 终局状态（未发起＝null）。</summary>
    public TargeterStatus? LastTargetingStatus { get; private set; }

    /// <summary>本轮 targeting 终局原因（成功＝null；取消/失败＝类别）。</summary>
    public TargeterFailureReason? LastTargetingReason { get; private set; }

    /// <summary>选中项定义标识（成功终局后读出；未到达＝null）。</summary>
    public string? LastSelectedId { get; private set; }

    /// <summary>落位结果（未到达＝null）。</summary>
    public CardPlaceResult? LastPlaceResult { get; private set; }

    /// <summary>最近一次异常（异常隔离前提下的可断言面；无＝null）。</summary>
    public Exception? LastError { get; private set; }

    private async Task OnCastAsync(C2CastView view, Context ctx, CancellationToken ct)
    {
        try
        {
            RunCount += 1;
            LastPool = Array.Empty<string>();
            LastDrawnIds = Array.Empty<string>();
            PoolEmptyTerminated = false;
            LastTargetingStatus = null;
            LastTargetingReason = null;
            LastSelectedId = null;
            LastPlaceResult = null;

            // 上下文取用面（「卡 → 玩家 → 服务」；运行期由效果自身完成——不注入、不旁路）
            var random = MatchRandomService.ResolveFor(Host);
            var cards = MatchCardService.ResolveFor(Host);
            var targeters = TargeterManager.ResolveFor(Host);
            if (random is null || cards is null || targeters is null)
            {
                return; // 脱局降级：功能不可用、不抛错、不失败
            }

            ServicesResolved = true;

            if (Host is not CardBase host || host.Owner is not { } owner)
            {
                return;
            }

            // ① 卡池（本局卡牌库已注册定义的全体——经枚举读面）＋效果侧筛选（自备候选集）＋规范化排序
            LastPool = cards.RegisteredDefinitions.Keys
                .Where(id => _poolFilter is null || _poolFilter(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            // ② 先判池（组装护栏）：池空＝链级终止——不构造选择器（空名单会触发构造期 fail-fast 通道）、零副作用
            if (LastPool.Count == 0)
            {
                PoolEmptyTerminated = true;
                return;
            }

            // ③ 取样（经对局随机服务：不放回、名单互异；n＝min(N, 池数)——N 为期望上限、非硬性）
            var take = Math.Min(_drawCount, LastPool.Count);
            LastDrawnIds = random.PickN(LastPool, take);

            // ④ 构造卡牌选择器（名单形态——定义级标识＋可读名称）→ 出题
            var listings = LastDrawnIds
                .Select(id => new CardListing(id, cards.RegisteredDefinitions[id].Name))
                .ToArray();
            var context = new TargetingRequestContext().WithSlotListings(C2Kit.SlotName, listings);
            var targeter = targeters.CreateTargeter(
                slots: new TargetSlot[] { new CardPickerSlot(CardPickerForm.Listing, 1, 1, C2Kit.SlotName) },
                context: context);

            var targeting = await targeter.Targeting();
            LastTargetingStatus = targeting.Status;
            LastTargetingReason = targeting.Reason;

            // ⑤ 取消/失败＝链终止（零副作用——不生成、不落位）
            if (targeting.Status != TargeterStatus.Ok)
            {
                return;
            }

            LastSelectedId = targeting.Outcome!.GetIdentifiers(C2Kit.SlotName).Single();

            // ⑥ 生成实例（S9 创建面）＋落位（S9 统一放置服务——去向为显式调用点）
            LastPlaceResult = _destination switch
            {
                C2Destination.Hand => await cards.CreateAndPlaceToHandAsync(LastSelectedId, owner, ct),
                C2Destination.DeckTop => await cards.CreateAndPlaceToDeckTopAsync(LastSelectedId, owner, ct),
                _ => throw new InvalidOperationException($"未知去向（{_destination}）——调用方构造错误。"),
            };
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
