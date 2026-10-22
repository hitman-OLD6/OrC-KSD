using Orc.Cards;
using Orc.Game.Board;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// S2（隐蔽机制——状态承载／豁免接线／揭示／信号／读取面）：
// ①隐蔽读取面（CovertRules.IsCovert——单位级布尔判定；「隐蔽单位」筛选/豁免剔除判断/对抗条件的对接读口；
//   权威依据＝「隐蔽」标记（KeywordIds.Covert）——实例当前内容即真源）。
// ②「隐蔽单位」列表/枚举读口（CollectCovertUnits——查询面；按 Q&A-1「查询/计数不受限」、不经豁免剔除；
//   列表级筛选由使用方基于本读口组合——S2 不建筛选器基础设施）。
// ③揭示服务（CovertRules.RevealAsync——揭示动作的唯一标准发动入口；先移除隐蔽标记→调揭示逻辑→
//   广播 unit.revealed）。「揭示：X」内容经卡侧「揭示触发器」统一承载（见 UnitCard）；
//   效果层接入＝EffectRuntime.RevealAsync（同构三层）。
// ④对外契约常量：「揭示触发器」稳定名（TriggerName——供 S3 模板效果按名对接）。
// 豁免接线（剔除点）不在本文件：判定器默认规则（TargetEligibilityJudicator）／无头选靶
// （EffectTargetResolveJudicator）／光环受益收集（GameEnvironment.CollectAuras）／钳击配对（PincerRules）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 隐蔽机制规则助手（S2；静态便利口——对齐 <see cref="VeteranRules"/>/<see cref="SuppressRules"/> 服务先例）：
/// 读取面（「是否隐蔽」判定＋「隐蔽单位」列表读口）＋对外契约常量（触发器稳定名）＋揭示服务。
/// 揭示动作的发动经 <see cref="RevealAsync"/>（唯一标准发动入口——须经本口、不得绕过直调内部步骤）。
/// </summary>
public static class CovertRules
{
    /// <summary>
    /// 「揭示触发器」标识名（S2 定稿——**对 S3 契约**：揭示动作的内容承载面；由卡牌的揭示触发效果
    /// （「揭示：X」内容）经效果预制体 <c>injects</c> 按名注入；触发它＝执行揭示内容，
    /// 随后由 <see cref="RevealAsync"/> 收尾广播 <c>unit.revealed</c>）。风格对齐「老兵触发器」「部署词条触发器」等具名先例。
    /// </summary>
    public const string TriggerName = "揭示触发器";

    /// <summary>
    /// 单位级「是否隐蔽」公共判定读口（S2；「隐蔽单位」筛选/豁免剔除判断/对抗条件的对接面；权威依据＝「隐蔽」标记——
    /// 实例当前内容即真源）：当前实例内容含「隐蔽」标记＝true；null/未装载词条面的实体＝false 降级、不抛错
    /// （对齐 <see cref="VeteranRules.IsVeteran"/> 先例）；死亡后按既有词条查询面语义只读可用（登记保留）。
    /// 语义＝单位级布尔判定（列表级「隐蔽单位」筛选由使用方基于本读口与既有遍历/筛选面组合——
    /// 或直接经 <see cref="CollectCovertUnits"/>）；豁免剔除实现侧同经本读口（全库单一判据、避免多源）。
    /// </summary>
    public static bool IsCovert(Card? card)
        => card is not null && KeywordRules.HasKeyword(card, KeywordIds.Covert);

    /// <summary>
    /// 「隐蔽单位」列表读口（S2；查询面——按 Q&A-1「查询/计数不受限」、**不经豁免剔除**）：
    /// 枚举战场三线（玩家A 支援线 → 前线 → 玩家B 支援线，线内索引序）中当前处于隐蔽的单位；
    /// 不分敌我（归属筛选由使用方组合）；无隐蔽单位＝空列表（不抛错）。
    /// 用途＝「揭示 1 个隐蔽单位」类编排的候选来源（管理动作域——揭示选靶不属「效果作用对象」路径）
    /// 与「每有 1 个隐蔽单位…」类查询/计数。
    /// </summary>
    /// <exception cref="ArgumentNullException">environment 为 null。</exception>
    public static IReadOnlyList<UnitCard> CollectCovertUnits(GameEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var result = new List<UnitCard>();
        foreach (var line in new[]
                 {
                     environment.Battlefield.PlayerASupportLine,
                     environment.Battlefield.FrontLine,
                     environment.Battlefield.PlayerBSupportLine,
                 })
        {
            foreach (var slot in line.Members)
            {
                if (slot is UnitCard unit && IsCovert(unit))
                {
                    result.Add(unit);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 揭示服务（S2；揭示动作的**唯一标准发动入口**——效果层经 <c>EffectRuntime.RevealAsync</c>、
    /// 测试/机制级全链直接经本口；「揭示：X」内容经卡侧「揭示触发器」统一承载，不得绕过直调内部步骤）。
    /// <para>流程＝只对「当前处于隐蔽的单位」发生：①移除「隐蔽」标记（先落定）→ ②揭示逻辑（触发「揭示触发器」——
    /// 内容经效果预制体注入；未注入 handler＝空转、无副作用）→ ③广播 <c>unit.revealed</c>（恰一次——
    /// 揭示落定后；观察者所见即终态：本人揭示内容已生效）。</para>
    /// <para>幂等（二态经返回结果程序化区分）：已揭示（标记已移）/非隐蔽 = NoOp（零信号、不执行揭示逻辑、不抛错）；
    /// 未在场上/已死亡（防御细化——揭示只对场上存活单位有意义，且终态单位走词条移除链会被拒绝）
    /// = NoOp；执行中重入＝无操作（防重入环——标记先行移除使重入时不再处于隐蔽，见 <see cref="RevealOutcome.NoOp"/>）。</para>
    /// <para>信号语义＝「某单位完成了一次真实揭示」（先落定后发射、恰一次）；「机制性清理」（死亡注销等）
    /// 不属揭示、不发本信号。取消类异常穿透上抛（框架既有语义）；参数契约错误（null）＝fail-fast。</para>
    /// </summary>
    /// <param name="unit">被揭示单位（须为当前隐蔽的场上单位；否则＝NoOp）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>揭示结局（<see cref="RevealOutcome.Revealed"/> / <see cref="RevealOutcome.NoOp"/>）。</returns>
    /// <exception cref="ArgumentNullException">unit 为 null（参数契约错误——fail-fast）。</exception>
    public static async Task<RevealOutcome> RevealAsync(UnitCard unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        // 前置：当前处于隐蔽的**场上存活**单位方发生揭示；其余＝幂等无操作（零信号、不执行揭示逻辑、不抛错）。
        if (!IsCovert(unit)
            || !unit.TryGetData<UnitStateData>(out var state)
            || state.IsDestroyed
            || state.Line is null)
        {
            return RevealOutcome.NoOp;
        }

        // ① 先移除隐蔽标记（落定——先于揭示逻辑；重入防护：执行中重入时标记已移除 ⇒ 幂等 NoOp、同次揭示至多一次信号）。
        await unit.Keywords.RevokeAsync(KeywordIds.Covert).ConfigureAwait(false);

        // ② 揭示逻辑（卡侧「揭示触发器」统一承载「揭示：X」内容；无注入＝空转——信号照发，对齐「无老兵触发效果时升级照跑」）。
        await unit.InvokeRevealTriggerAsync(ct).ConfigureAwait(false);

        // ③ 机制宣告：unit.revealed（恰一次——揭示落定后；先落定、后发射）。
        await unit.EmitUnitRevealedAsync(ct).ConfigureAwait(false);

        return RevealOutcome.Revealed;
    }
}

/// <summary>揭示服务结局（二态；S2 锁定——揭示发生/无操作必须可程序化区分）。</summary>
public enum RevealOutcome
{
    /// <summary>揭示发生（标记移除 ＋ 揭示逻辑执行 ＋ <c>unit.revealed</c> 恰一次）。</summary>
    Revealed,

    /// <summary>幂等无操作（非隐蔽/已揭示/未在场上/已死亡——零信号、不执行揭示逻辑、不抛错）。</summary>
    NoOp,
}
