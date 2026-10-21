namespace Orc.Game.Sandbox;

// ─────────────────────────────────────────────────────────────────────────────
// 沙盒调试原语：失败原因常量（wire 字符串）。
// 口径：一次原语调用 = 一个明确结局；失败一律经 SandboxPrimitiveResult.Reason 回传，
// 不抛异常（Trigger.InvokeAsync 会吞 handler 异常并记 Normal——抛等于静默成功）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>沙盒原语失败原因常量（wire 字符串，kebab-case；与引擎枚举对应关系见各常量注释）。</summary>
public static class SandboxFailure
{
    /// <summary>当前引擎实例未获沙盒授权（装配期决定、缺省未授权）。</summary>
    public const string NotAuthorized = "sandbox-not-authorized";

    /// <summary>对局未就绪（<c>Match.Sandbox</c> 未装配／非"进行"态）。</summary>
    public const string MatchNotReady = "match-not-ready";

    /// <summary>参数非法（缺字段／类型或值域不符）——Handler 首行兜底口径。</summary>
    public const string InvalidArguments = "invalid-arguments";

    /// <summary>卡定义未注册（引擎 <see cref="KeyNotFoundException"/>）。</summary>
    public const string DefinitionUnknown = "definition-unknown";

    /// <summary>非单位卡不能落到阵线（引擎 <see cref="ArgumentException"/>／类别不匹配）。</summary>
    public const string NotAUnitCard = "not-a-unit-card";

    /// <summary>槽位索引越界（须 0..4）。</summary>
    public const string SlotOutOfRange = "slot-out-of-range";

    /// <summary>槽位不在该玩家的指定线内（前置检查；原子层不接受线归属校验）。</summary>
    public const string SlotNotOwned = "slot-not-owned";

    /// <summary>目标槽位已被占用（<see cref="CardPlaceFailureReason.TargetSlotOccupied"/>／<see cref="PlayFailureReason.TargetSlotOccupied"/>）。</summary>
    public const string TargetSlotOccupied = "target-slot-occupied";

    /// <summary>单位已单位化（<see cref="CardPlaceFailureReason.UnitAlreadyUnitized"/>／<see cref="PlayFailureReason.UnitAlreadyUnitized"/>）。</summary>
    public const string UnitAlreadyUnitized = "unit-already-unitized";

    /// <summary>实体不存在（引用键无法解析）。</summary>
    public const string EntityNotFound = "entity-not-found";

    /// <summary>实体已销毁（生命周期已终止）。</summary>
    public const string EntityDestroyed = "entity-destroyed";

    /// <summary>实体不在战场（移动原语前置）。</summary>
    public const string NotOnBattlefield = "not-on-battlefield";

    /// <summary>目标槽位与当前位置相同（移动原语）。</summary>
    public const string SameSlot = "same-slot";

    /// <summary>卡组已空（抽牌原语）。</summary>
    public const string DeckEmpty = "deck-empty";

    /// <summary>对局已结束（<see cref="PlayFailureReason.GameEnded"/>）。</summary>
    public const string GameEnded = "game-ended";

    /// <summary>非"进行"相位（<see cref="PlayFailureReason.PhaseBlocked"/>）。</summary>
    public const string PhaseBlocked = "phase-blocked";

    /// <summary>放置被拒（<see cref="CardPlaceFailureReason.PlacementRejected"/>／其他打出链失败）。</summary>
    public const string PlacementRejected = "placement-rejected";

    /// <summary>点数越界（int 溢出，防御性）。</summary>
    public const string PointsOutOfRange = "points-out-of-range";

    // ---------- 工具层附加 warning（成功语义，不是失败） ----------

    /// <summary>请求点数超过指挥点槽数（不拒绝、不截断；成功 + warning）。</summary>
    public const string WarningPointsExceedSlots = "points-exceed-slots";

    /// <summary>实际点数与请求值不一致（判定器改写了增量；成功 + warning，附 requested/actual）。</summary>
    public const string WarningPointsAdjusted = "points-adjusted";
}
