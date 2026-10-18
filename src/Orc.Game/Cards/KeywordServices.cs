using Orc.Cards;
using Orc.Core;

namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 第 2 批·A2 新增词条的服务面（施加入口 / 读取面 / 待办环节——口径依据《需求文档（A2）》）：
// 压制（施加/延长/查询）、抑制（清空处置）、动员（批 1 效果化：直调成员退役——保留 public 面壳）、
// 重甲（批 2 效果化：减伤本体迁效果承载——保留增改/读点面）、对战词条（筛选/计数/集合读取）、
// 情报（待办环节）。钳击关系体系见 PincerSystem.cs。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 压制服务（「压制动作」的机制入口面；Q&A-5/Q&A-10）：施加＝对位防护判定（「无法被压制」→ 拒绝、无副作用）
/// → 授予「被压制」（参值＝剩余拥有者回合数、默认 1）。延长＝「额外压制一回」＝剩余回合数 +1（承载面）。
/// 行为消费（不能移动或攻击）在指挥可用性/复验侧读标记；解除时点由被压制效果自驱动（拥有者回合结束；
/// 批 1 效果化——状态管理由 <see cref="SuppressionLifecycleEffect"/> 承载）。
/// </summary>
public static class SuppressRules
{
    /// <summary>压制默认持续（剩余拥有者回合数＝1——官方口径「下一个回合结束时」）。</summary>
    public const int SuppressedDefaultTurns = 1;

    /// <summary>
    /// 施加压制（施加前资格判定：无法被压制 → 拒绝、无副作用——拒绝而非部分执行）。
    /// 返回＝是否施加（已死亡目标 / 资格拒绝＝false）。
    /// </summary>
    public static Task<bool> ApplyAsync(UnitCard target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (IsDead(target))
        {
            return Task.FromResult(false); // 已死亡：拒绝（零副作用）
        }

        if (target.Keywords.Has(KeywordIds.CannotBeSuppressed))
        {
            return Task.FromResult(false); // 资格判定：无法被压制 → 拒绝（零副作用、不消耗）
        }

        return target.Keywords.GrantAsync(KeywordIds.Suppressed, SuppressedDefaultTurns);
    }

    /// <summary>是否被压制（条件查询：消灭被压制单位 / 压制移除类消费的读口）。</summary>
    public static bool IsSuppressed(Card card) => KeywordRules.HasKeyword(card, KeywordIds.Suppressed);

    /// <summary>
    /// 延长压制（「额外压制一回」——剩余拥有者回合数 +1；承载面示例）。
    /// 返回＝是否延长（目标无「被压制」标记＝false、无副作用）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">extraTurns ≤ 0。</exception>
    public static Task<bool> ExtendAsync(UnitCard target, int extraTurns = 1)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(extraTurns);
        if (IsDead(target) || !target.Keywords.Has(KeywordIds.Suppressed))
        {
            return Task.FromResult(false);
        }

        var current = target.Keywords.GetValue(KeywordIds.Suppressed) ?? SuppressedDefaultTurns;
        target.Keywords.SetValue(KeywordIds.Suppressed, current + extraTurns); // 「额外压制一回」＝＋1
        return Task.FromResult(true);
    }

    private static bool IsDead(UnitCard unit)
        => unit.TryGetData<UnitStateData>(out var state) && state.IsDestroyed;
}

/// <summary>
/// 抑制服务（「抑制动作」的机制入口面；Q&A-4/Q&A-11）。
/// 清空处置（「抑制＝剥光」）：①清空词条（除「被抑制」外全部——含其它施加物与防护标记——字面全量、无保护名单）；
/// ②清空所有修饰器；③清除所有额外效果（含光环声明；词条内嵌效果随词条清除覆盖）；
/// ④授予「被抑制」（保留标记、供条件查询）。
/// 防御口径（2026-10-18 更正）：抑制只重置**上限**（清修饰器后的基准）——损伤量不碰；
/// 当前值＝上限−损伤：加防增益被清后高于新上限部分自然落回上限，本来低于上限（已受伤）则保持不变
/// （不回血、不抹伤害）。攻击力/行动花费同属清修饰器后的自然回落。
/// 施加前资格判定：「无法被抑制」→ 拒绝（零副作用）。
/// </summary>
public static class InhibitRules
{
    /// <summary>
    /// 施加抑制（清空处置全链）。返回＝是否执行（已死亡 / 资格拒绝＝false——拒绝而非部分执行）。
    /// </summary>
    public static async Task<bool> ApplyAsync(UnitCard target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (IsDead(target))
        {
            return false; // 已死亡：拒绝（零副作用）
        }

        if (target.Keywords.Has(KeywordIds.CannotBeInhibited))
        {
            return false; // 施加前资格判定：无法被抑制 → 拒绝、无副作用
        }

        // ① 清空词条（除「被抑制」外全部——含其它施加物与防护标记）
        await target.Keywords.ClearAllExceptAsync(KeywordIds.Inhibited, ct);

        // ② 清空所有修饰器（攻击力/防御力上限/行动花费的自然回落载体）
        await target.Modifiers.ClearAllAsync(ct);

        // ③ 清除所有额外效果（含光环声明；主动/被动效果全撤；词条内嵌效果已随词条清除覆盖）
        foreach (var effect in target.Effects.ToArray())
        {
            target.RemoveEffect(effect);
        }

        // ④ 授予「被抑制」（保留标记——重复施加场景＝幂等无操作）
        await target.Keywords.GrantAsync(KeywordIds.Inhibited);
        return true;
    }

    /// <summary>是否被抑制（条件查询承载）。</summary>
    public static bool IsInhibited(Card card) => KeywordRules.HasKeyword(card, KeywordIds.Inhibited);

    private static bool IsDead(UnitCard unit)
        => unit.TryGetData<UnitStateData>(out var state) && state.IsDestroyed;
}

/// <summary>
/// 动员服务（Q&A-6；批 1 效果化后的保留壳）：原「受伤害直调」成员（<c>OnUnitDamagedAsync</c>）已退役——
/// 「受到实际伤害（净伤害＞0）后失去动员」由动员效果（<see cref="MobilizeLossEffect"/>——批 4 数据化行为引用目标）监听
/// <see cref="GameUpdates.CardDamaged"/> 信号自我撤销承载（受伤害门户不再感知动员——直调路径删除，
/// 均为本批迁移范围内的内部专用面处置）；其余读取能力经既有词条读取面（<see cref="KeywordRules"/>）承担。
/// 类保留为 public 面无成员壳（本批不做公共面删除、不夹带替代成员）。
/// </summary>
public static class MobilizeRules
{
}

/// <summary>
/// 重甲服务（Q&A-7）：值读取 / 「+1 重甲」增改面 / 「无视重甲」判定读点（承载面）。
/// 减伤本体在重甲效果（<see cref="ArmorReductionEffect"/>——「造成攻击伤害」handler 链介入、参值运行期读取）；
/// 本面为调用方便利入口。
/// </summary>
public static class ArmorRules
{
    /// <summary>
    /// 「无视重甲」标识（判定读点字面——对外数据契约基线）：内容层效果后续经词条注册＋授予接通；
    /// 本批未注册（授权内建清单固定 10 项）——读点就位、默认不无视（查询恒 false、行为正确）。
    /// </summary>
    public const string ArmorPiercingId = "无视重甲";

    /// <summary>读取重甲值（无词条/无参值＝0）。</summary>
    public static int GetValue(Card card) => KeywordRules.GetKeywordValue(card, KeywordIds.Armor) ?? 0;

    /// <summary>攻击者侧「无视重甲」判定读点（承载面；默认不无视——见 <see cref="ArmorPiercingId"/>）。</summary>
    public static bool IgnoresArmor(Card attacker)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        return KeywordRules.HasKeyword(attacker, ArmorPiercingId);
    }

    /// <summary>
    /// 「+1 重甲」增改（无重甲单位视为 0 → 得 1；重复叠加即递增）。
    /// 写入/增改均受 [0, 3] 钳制（值封顶——词条层校验/封顶；组件侧同钳，双保险）。
    /// </summary>
    public static async Task AddArmorAsync(CardBase card, int amount)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Keywords.Has(KeywordIds.Armor))
        {
            var current = card.Keywords.GetValue(KeywordIds.Armor) ?? 0;
            var target = Math.Clamp(current + amount, ArmorKeywordComponent.MinValue, ArmorKeywordComponent.MaxValue);
            card.Keywords.SetValue(KeywordIds.Armor, target);
            return;
        }

        var granted = Math.Clamp(amount, ArmorKeywordComponent.MinValue, ArmorKeywordComponent.MaxValue);
        await card.Keywords.GrantAsync(KeywordIds.Armor, granted);
    }
}

/// <summary>
/// 对战词条读取面（Q&A-2）：按标记筛选（集合读取）/计数/条件查询。
/// 集合元素＝词条标识（带参值词条的参值经统一读口另读）；顺序＝打标全集登记序（稳定）。
/// 「获得 1 个随机对战词条」＝池构建＋PickOne＋授予（组合——调用方从
/// <see cref="KeywordRegistry.BattleKeywordUniverse"/> 构建池、经对局随机服务取样；参值词条默认参值 1，工程约定）；
/// 「获得全部对战词条」＝集合读取＋逐个授予（组合）。不新增专门机制。
/// </summary>
public static class BattleKeywordRules
{
    /// <summary>读取某卡的对战词条集合（按「已实现且打标」筛选；登记序稳定）。</summary>
    public static IReadOnlyList<string> GetBattleKeywords(Card card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var result = new List<string>();
        foreach (var keyword in KeywordRegistry.BattleKeywordUniverse)
        {
            if (KeywordRules.HasKeyword(card, keyword))
            {
                result.Add(keyword);
            }
        }

        return result;
    }

    /// <summary>对战词条数（计数面）。</summary>
    public static int CountBattleKeywords(Card card) => GetBattleKeywords(card).Count;

    /// <summary>条件查询：「对战词条数不小于 n」（筛选面承载）。</summary>
    public static bool HasAtLeastBattleKeywords(Card card, int count) => CountBattleKeywords(card) >= count;
}

/// <summary>
/// 情报待办环节（Q&A-8）：触发结构已就位（「具有情报的卡被使用时」→ 本点）；「情报执行」（明牌 X 张）因
/// G18 明牌系统未就绪＝待接线——本点即明确的待办环节。默认实现＝写引擎日志（触发留痕）；测试可注入桩
/// 以验证触发结构（注入后建议恢复原值）。G18 就绪后替换为实际执行并申报解除。
/// </summary>
public static class IntelligenceRules
{
    /// <summary>待办处理器（触发点调用；参数＝被使用的卡、情报 X、引擎〔可空——脱局防御〕、取消令牌）。</summary>
    public static Func<Card, int, LogicEngine?, CancellationToken, Task> PendingRevealHandler { get; set; }
        = WritePendingRevealLog;

    /// <summary>触发点调用（情报效果在 card.played 命中本卡时——批 1 效果化后由 <see cref="IntelligenceTriggerEffect"/> 驱动）。</summary>
    internal static Task InvokePendingRevealAsync(Card card, int amount, LogicEngine? engine, CancellationToken ct)
        => PendingRevealHandler(card, amount, engine, ct);

    /// <summary>默认待办实现：写引擎日志（触发留痕；G18 明牌系统就绪后替换为实际执行）。</summary>
    private static Task WritePendingRevealLog(Card card, int amount, LogicEngine? engine, CancellationToken ct)
    {
        engine?.RootStream.WriteLog(
            "情报",
            $"情报 {amount}：「明牌 {amount} 张」执行待接线（G18 明牌系统未就绪）。",
            LogLevel.Info,
            new[] { "keyword", "intelligence" });
        return Task.CompletedTask;
    }
}
