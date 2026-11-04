using System.Security.Cryptography;
using System.Text;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Managers;
using Orc.Game.Players;
using Orc.Game.EffectParsing;

namespace Orc.Game.Effects;

/// <summary>
/// 效果运行时门面的**动作扩展面**（S3–S5 加性；partial 续篇——主文件已 1300＋ 行）：
/// 效果攻击（R4）／合法攻击候选枚举（R4）／按条件检索卡组（R5）／动态效果授予四方法（R6）。
/// <para>
/// **失败语义**（与 <see cref="Orc.Game.Cards.KeywordEffectActionFace"/> 的分工见「门面分工」）：
/// 本门面服务卡效果 csx，**失败必须可见**——授予/编译类失败抛 <see cref="InvalidOperationException"/>；
/// 检索的「卡组里没有空军」是**卡面的正常结局**（<see cref="TutorResult"/> 的 <c>NotFound</c>，不抛）。
/// </para>
/// <para>
/// 冻结面提示：本文件新增的 public 成员逐条进 <c>tests/Orc.Game.Tests/Baselines/contract-freeze.json</c>
/// （<c>ContractFreezeTests</c> 反射比对）——改签名算契约变更。
/// </para>
/// </summary>
public sealed partial class EffectRuntime
{
    // ---------- R4：效果攻击与合法攻击候选 ----------

    /// <summary>
    /// **效果发起的攻击**（R4 受控入口）：转发既有 <see cref="CommandManager.AttackByEffectAsync"/>，走**完整攻击链**
    /// （复验 → 六重目标合法性〔归属/存活在场/烟幕/守护资格/拦截/范围〕→ 处理端结算）。
    /// <para>
    /// 语义＝**零行动经济面**：不扣指挥点、不写行动位、不发 <c>unit.acted</c>、不发 <c>card.played</c>、
    /// **不受压制阻断**（压制只决定「能否被指挥」）。结算成功发恰一次 <c>unit.attacked</c>（<c>ByEffect=true</c>）。
    /// </para>
    /// </summary>
    /// <param name="attacker">发起攻击的在场单位。</param>
    /// <param name="target">被攻击方（敌方单位或敌方 HQ）。</param>
    /// <param name="source">归因参数（本批攻击链的 <c>triggerCard</c> 固定为攻击者自身，本形参不参与）。</param>
    /// <returns><c>true</c>＝本次攻击结算完成；目标不合法/单位不在场/服务不可达＝<c>false</c>（零副作用）。</returns>
    /// <exception cref="ArgumentNullException">attacker / target 为 null。</exception>
    public async Task<bool> AttackByEffectAsync(
        Card attacker, Card target, Card? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);

        if (_commands() is not { } commands || attacker is not UnitCard unit)
        {
            return false; // 服务不可达/非单位＝不执行（零副作用、不抛错）
        }

        return await commands.AttackByEffectAsync(unit, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// **合法攻击候选枚举**（R4）：与指挥入口**同源**——遍历结构与逐候选判定全部来自
    /// <see cref="CommandManager.AttackCandidatesOf"/>（同一 <c>combat.target.legal</c> 判定器链，不另写一套）。
    /// <para>顺序＝敌支援线 → 前线 → 敌方 HQ；烟幕单位、守护违规目标、射程外目标不在其中。</para>
    /// <para>服务不可达/非单位＝空集（功能不可用、不失败）。</para>
    /// </summary>
    /// <param name="attacker">发起方单位。</param>
    /// <exception cref="ArgumentNullException">attacker 为 null。</exception>
    public IReadOnlyList<Card> AttackCandidates(Card attacker)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        return _commands() is { } commands && attacker is UnitCard unit
            ? commands.AttackCandidatesOf(unit)
            : Array.Empty<Card>();
    }

    // ---------- R5：按条件检索卡组 ----------

    /// <summary>
    /// **检索（兵种数组版）**（R5 主用形态；**csx 专属**——见下方排除清单说明）：从 <paramref name="viewer"/> 归属玩家的卡组**顺序**取第一张
    /// 兵种命中 <paramref name="unitTypes"/> 的卡，抽到手（例：空军＝<c>["fighter","bomber"]</c>）。
    /// <para>
    /// 命中走既有抽牌链路（<c>card.drawn</c> → <c>card.hand.add</c>）；满手＝爆牌裁决（<c>card.drawn</c> →
    /// 销毁 → <c>card.burned</c>）。**无匹配＝<see cref="TutorStatus.NotFound"/>**（不抽牌、不发信号、不报错——
    /// 这是卡面正常结局，不是故障）。
    /// </para>
    /// </summary>
    /// <param name="viewer">视角卡（卡组归属＝该卡的归属玩家）。</param>
    /// <param name="unitTypes">兵种标识集合（<see cref="Orc.Game.Cards.UnitType"/> 字面值；大小写敏感）。</param>
    /// <param name="source">归因参数（本批检索链路的下游信号不带 Source，保留形参以统一 csx 书写）。</param>
    /// <exception cref="ArgumentNullException">viewer / unitTypes 为 null。</exception>
    /// <exception cref="InvalidOperationException">视角卡未加载/非本局引用（装配性错误——不静默空转）。</exception>
    [Orc.Game.Effects.HandlerDsl.HandlerDslResult("status:string:enum", "card:entity")]
    public Task<TutorResult> TutorFromDeckAsync(
        Card viewer, IReadOnlyList<string> unitTypes, Card? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(unitTypes);

        var types = new HashSet<string>(
            unitTypes.Where(static type => !string.IsNullOrWhiteSpace(type)), StringComparer.Ordinal);
        return TutorCoreAsync(viewer, card => MatchesUnitType(card, types), ct);
    }

    /// <summary>
    /// **检索（谓词版）**（R5 逃生舱）：与数组版**同一实现内核**——只是把「兵种命中」换成调用方谓词。
    /// <para>
    /// 含 <see cref="Func{T,TResult}"/> 形参，**不可映射到 handler DSL 引脚封闭集**，故进排除清单
    /// （<c>contracts/handler-dsl/v1/EXCLUSIONS.md</c> 与 <c>HandlerDslExclusions</c> 镜像）——只能脚本节点。
    /// </para>
    /// </summary>
    /// <param name="viewer">视角卡（卡组归属＝该卡的归属玩家）。</param>
    /// <param name="predicate">匹配谓词（对卡组顺序逐张求值，首个命中者被抽出）。</param>
    /// <param name="source">归因参数（同数组版）。</param>
    /// <exception cref="ArgumentNullException">viewer / predicate 为 null。</exception>
    /// <exception cref="InvalidOperationException">视角卡未加载/非本局引用（装配性错误——不静默空转）。</exception>
    /// <remarks>
    /// **本重载与数组版都进排除清单**（见 <c>contracts/handler-dsl/v1/EXCLUSIONS.md</c>）：
    /// 数组版的 <c>list&lt;string&gt;</c> 引脚在 v1 表达式里**没有产出形态**（图形化无法填值），
    /// 故两个重载都只能脚本节点——目录里不出现 <c>tutorFromDeck</c> 节点。
    /// </remarks>
    public Task<TutorResult> TutorFromDeckAsync(
        Card viewer, Func<Card, bool> predicate, Card? source = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(predicate);
        return TutorCoreAsync(viewer, predicate, ct);
    }

    /// <summary>
    /// 检索共用内核：卡组顺序扫描 → 首个命中者经**既有定向取卡动作**入手（不另立抽牌/爆牌语义）。
    /// </summary>
    private async Task<TutorResult> TutorCoreAsync(Card viewer, Func<Card, bool> predicate, CancellationToken ct)
    {
        // 引用契约：未加载/非本局引用＝装配性错误（明确失败，绝不静默退化成"没抽到"）。
        if (OwnerOf(viewer) is not { } player || !ReferenceEquals(ResolveFor(viewer), this))
        {
            throw new InvalidOperationException(
                $"检索的视角卡 '{viewer.Name}' 未加载或不属于本对局（无法推导卡组归属）——检索动作被拒绝。");
        }

        if (_players() is not { } players)
        {
            throw new InvalidOperationException(
                $"检索的视角卡 '{viewer.Name}' 无可用的玩家管理器（服务未装配）——检索动作被拒绝。");
        }

        // 卡组顺序（索引 0＝顶）——确定性、非随机；首个命中者胜出。
        CardBase? match = null;
        for (var index = 0; index < player.Deck.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (player.Deck.PeekInstance(index) is { } candidate && predicate(candidate))
            {
                match = candidate;
                break;
            }
        }

        if (match is null)
        {
            return TutorResult.NotFound(); // 扫完全卡组无匹配：零副作用（不抽牌、不发信号、不报错）
        }

        // 命中 → 既有定向取卡动作（card.drawn → card.hand.add；满手 = drawn → 销毁 → card.burned）。
        var fetched = await players.FetchFromDeckAsync(player, match, ct).ConfigureAwait(false);
        return fetched.Status switch
        {
            DeckFetchStatus.Fetched => TutorResult.Fetched(match),
            DeckFetchStatus.Burned => TutorResult.Burned(match),
            _ => throw new InvalidOperationException(
                $"检索命中后取卡被拒绝（'{match.Name}'，原因＝{fetched.FailureReason}）——"
                + "扫描与取件之间的状态冲突，属装配性问题。"),
        };
    }

    private static bool MatchesUnitType(Card card, HashSet<string> unitTypes)
    {
        if (unitTypes.Count == 0 || card is not CardBase cardBase)
        {
            return false;
        }

        foreach (var unitType in cardBase.Definition.UnitTypes)
        {
            if (unitTypes.Contains(unitType))
            {
                return true;
            }
        }

        return false;
    }

    // ---------- R6：动态效果授予（薄转发；底层能力既有） ----------

    /// <summary>
    /// **动态授予效果**（R6-1）：把已注册的效果预制体挂到 <paramref name="target"/> 上，**同帧后续动作即可见新边**
    /// （转发 <see cref="EffectGrantService.Grant"/>）。
    /// </summary>
    /// <param name="target">宿主卡（须已在编排运行时内）。</param>
    /// <param name="prefabId">效果预制体 id（已注册的组件资产）。</param>
    /// <param name="lifetime">生命周期（缺省随宿主销毁）。</param>
    /// <returns>挂载凭据（撤销靠它，不按效果 id 反查）。</returns>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    /// <exception cref="ArgumentException">prefabId 为空。</exception>
    /// <exception cref="InvalidOperationException">服务不可达；或宿主卡未进入编排运行时（未加载/已销毁）。</exception>
    /// <exception cref="PrefabLoadException">预制体未注册、不是组件，或装配失败（不留半装配状态）。</exception>
    public Task<GrantHandle> AttachPrefabAsync(
        Card target, string prefabId,
        GrantLifetime lifetime = GrantLifetime.UntilDestroyed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefabId);
        ct.ThrowIfCancellationRequested();

        var host = RequireGrantHost(target, prefabId);
        return Task.FromResult(EffectGrantService.Grant(host, prefabId, lifetime));
    }

    /// <summary>
    /// **动态授予效果（内联快照）**（R6-2）：先登记未注册的内联快照，再挂载。
    /// <para>已注册同 id 快照＝直接复用（不重复注册、不抛错）。</para>
    /// </summary>
    /// <param name="target">宿主卡（须已在编排运行时内）。</param>
    /// <param name="snapshot">效果快照（可以是调用方现造、尚未注册的实例）。</param>
    /// <param name="lifetime">生命周期（缺省随宿主销毁）。</param>
    /// <returns>挂载凭据。</returns>
    /// <exception cref="ArgumentNullException">target / snapshot 为 null。</exception>
    /// <exception cref="InvalidOperationException">服务不可达；或宿主卡未进入编排运行时。</exception>
    public Task<GrantHandle> AttachSnapshotAsync(
        Card target, EffectSnapshot snapshot,
        GrantLifetime lifetime = GrantLifetime.UntilDestroyed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        var host = RequireGrantHost(target, snapshot.Root.Id);
        RegisterSnapshot(host, snapshot);
        return Task.FromResult(EffectGrantService.Grant(host, snapshot.Root.Id, lifetime));
    }

    /// <summary>
    /// **动态授予效果（编译挂载）**（R6-3）：效果 DSL JSON 经编译驱动编成快照后挂载。
    /// <para>
    /// **编译资产不可用/编译失败＝明确失败**（抛 <see cref="InvalidOperationException"/>，不静默挂空壳）。
    /// 效果身份由 DSL 内容确定性派生（同文本⇒同 id、可重复挂载多份独立实例）。
    /// </para>
    /// </summary>
    /// <param name="target">宿主卡（须已在编排运行时内）。</param>
    /// <param name="effectDslJson">效果 DSL JSON 文本（<c>DslJson</c> 形态）。</param>
    /// <param name="lifetime">生命周期（缺省随宿主销毁）。</param>
    /// <returns>挂载凭据。</returns>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    /// <exception cref="ArgumentException">effectDslJson 为空。</exception>
    /// <exception cref="InvalidOperationException">服务不可达；宿主未就绪；或编译失败/无产物。</exception>
    public Task<GrantHandle> CompileAttachAsync(
        Card target, string effectDslJson,
        GrantLifetime lifetime = GrantLifetime.UntilDestroyed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectDslJson);
        ct.ThrowIfCancellationRequested();

        var host = RequireGrantHost(target, "<compiled>");

        var effectId = DeriveCompiledEffectId(effectDslJson);
        var result = new EffectCompilationDriver().CompileDsl(effectDslJson, effectId);
        if (result.Artifacts.Count == 0)
        {
            var reasons = result.Report.Failures.Count > 0
                ? string.Join("；", result.Report.Failures.Select(static failure => failure.Reason))
                : "编译资产不可用（无产物、无失败明细）";
            throw new InvalidOperationException(
                $"编译挂载失败：DSL 未能编译成效果快照（{reasons}）——不挂空壳。");
        }

        var snapshot = result.Artifacts[0].Snapshot;
        RegisterSnapshot(host, snapshot);
        return Task.FromResult(EffectGrantService.Grant(host, snapshot.Root.Id, lifetime));
    }

    /// <summary>
    /// **撤销动态效果**（R6-4）：凭挂载凭据撤销；**幂等**（已撤销再调＝无操作、不抛）。
    /// <para>禁止改成「按 prefabId 反查撤销」——同卡同效果多实例时无法仲裁（<see cref="GrantHandle"/> 才是唯一凭据）。</para>
    /// </summary>
    /// <param name="handle">挂载凭据。</param>
    /// <returns>本次调用是否真的执行了撤销（<c>false</c>＝此前已撤销）。</returns>
    /// <exception cref="ArgumentNullException">handle 为 null。</exception>
    public Task<bool> DetachEffectAsync(GrantHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var revoked = !handle.IsRevoked;
        handle.Revoke();
        return Task.FromResult(revoked);
    }

    /// <summary>授予宿主前置：卡形态 + 编排运行时 + 引擎可达（失败一律 <see cref="InvalidOperationException"/>）。</summary>
    private CardBase RequireGrantHost(Card target, string prefabId)
    {
        if (_engine?.Invoke() is null)
        {
            throw new InvalidOperationException(
                $"授予效果 '{prefabId}' 需要编排运行时（引擎未装配）——授予被拒绝。");
        }

        if (target is not CardBase host || host.ModelEntity is null)
        {
            throw new InvalidOperationException(
                $"卡牌 '{target.Name}' 尚未进入编排运行时（未加载或已销毁），无法授予效果 '{prefabId}'。");
        }

        return host;
    }

    /// <summary>
    /// 登记内联快照（两级：旧快照库 ＋ 编排运行时的预制体目录）。
    /// <para>
    /// **为什么必须两级**：<see cref="EffectGrantService.Grant"/> 走 <c>OrchestrationRuntime.GrantComponent</c>，
    /// 它按**预制体目录**解析类型；只把快照塞进旧快照库（<c>SnapshotStore</c>）会在装配期抛"未登记"。
    /// 故先经 <c>LegacySnapshotComponentBinder</c> 把旧快照无损转成 v4 组件并注册（含 handler 预编译）。
    /// 已注册同 id ＝直接复用（不重复编译）。
    /// </para>
    /// </summary>
    private void RegisterSnapshot(CardBase host, EffectSnapshot snapshot)
    {
        if (_engine?.Invoke() is not { } engine || host.ModelEntity is not { } model)
        {
            throw new InvalidOperationException("授予效果需要编排运行时（引擎未装配）——注册被拒绝。");
        }

        if (!engine.Prefabs.TryGetPrefab(snapshot.Root.Id, out _))
        {
            engine.Prefabs.RegisterPrefab(snapshot);
        }

        LegacySnapshotComponentBinder.Register(
            model.Runtime, snapshot, engine.ScriptEvaluator, engine.SyncScriptEvaluator);
    }

    /// <summary>编译挂载的效果身份：由 DSL 文本确定性派生（同文本⇒同 id，避免每次挂载都造新身份）。</summary>
    private static string DeriveCompiledEffectId(string effectDslJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(effectDslJson));
        return "compiled." + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
