namespace Orc.Game.Sandbox;

// ─────────────────────────────────────────────────────────────────────────────
// 沙盒调试原语：结果载体。
// 由原语入口创建、经触发数据交给内置 handler 回填，入口读回后对外返回。
// 业务失败一律写 Reason（不抛异常——Trigger.InvokeAsync 会把 handler 异常吞成 Normal）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 原语结局载体（可变、单次调用一份）：<see cref="Ok"/>＝成功；失败＝<see cref="Ok"/> false ＋
/// <see cref="Reason"/>（<see cref="SandboxFailure"/> 常量）；成功时按原语填充载荷字段。
/// </summary>
public sealed class SandboxPrimitiveResult
{
    /// <summary>是否成功（爆牌属成功语义）。</summary>
    public bool Ok { get; set; }

    /// <summary>失败原因（成功＝null）；取值为 <see cref="SandboxFailure"/> 常量。</summary>
    public string? Reason { get; set; }

    /// <summary>关联实体引用键（新建实例／被操作实体；点数与抽牌原语另有约定）。</summary>
    public string? Ref { get; set; }

    /// <summary>落点线名（<c>support</c> / <c>frontline</c>；未涉及＝null）。</summary>
    public string? Line { get; set; }

    /// <summary>落点槽位索引（0..4；未涉及＝null）。</summary>
    public int? Slot { get; set; }

    /// <summary>变更前值（点数原语＝原点数；其余＝null）。</summary>
    public object? Before { get; set; }

    /// <summary>变更后值（点数原语＝新点数；其余＝null）。</summary>
    public object? After { get; set; }

    /// <summary>是否因满手爆牌（成功语义：<c>CardPlaceStatus.Burned</c>）。</summary>
    public bool Burned { get; set; }

    /// <summary>成功附加告警（<c>points-exceed-slots</c> / <c>points-adjusted</c>；无＝null）。</summary>
    public string? Warning { get; set; }

    /// <summary>实际增量（点数原语）。</summary>
    public int? Delta { get; set; }

    /// <summary>实际落地的点数（点数原语；判定器改写后）。</summary>
    public int? Actual { get; set; }

    /// <summary>抽牌原语：实际进入手牌的卡引用键（顺序）。</summary>
    public List<string> Drawn { get; } = new();

    /// <summary>抽牌原语：被爆掉的卡引用键（顺序）。</summary>
    public List<string> BurnedRefs { get; } = new();

    /// <summary>成功结局。</summary>
    public static SandboxPrimitiveResult Success() => new() { Ok = true };

    /// <summary>失败结局（携带 wire 原因）。</summary>
    public static SandboxPrimitiveResult Fail(string reason) => new() { Ok = false, Reason = reason };
}
