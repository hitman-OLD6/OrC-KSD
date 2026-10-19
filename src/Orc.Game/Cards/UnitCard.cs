using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Board;
using Orc.Game.Managers;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Game.Triggers;

namespace Orc.Game.Cards;

/// <summary>
/// 单位卡（三大类之一）：部署入战场、参与战斗（2B 打出链就绪；2C：参与指挥/战斗与词条）。
/// 数据组件装配（E 区差异化）：＝阵营〔国籍〕＋部署费合并组件（基类）＋对战数据（本类构造装配；行动费/攻/防初始值）；
/// 单位数据 / 指挥组件于「单位化」时挂载（2B：单位化触发器默认事件——位置＝槽位、已毁＝false、类型＝从定义填充〔2C〕、三实时值＝对战组件值）。
/// 触发器（2B）：
/// ①预打出触发器（费用校验——指挥点验证；开始＝验证＋targeter 交互由打出管理器驱动）；
/// ②打出触发器（费用校验——外层复验；S9：默认链＝入口收尾〔unit.deployed → 扣费 → 离手 → 词条落点〕，
///   打出宣告／部署词条／单位化／card.placed 已下沉到「加入战场」动作）；
/// ③部署触发器（S9：默认链＝转发加入战场动作的**部署触发端**——代价校验 → card.played → 部署词条 → 共享处理端）；
/// ④加入触发器（S9：默认链＝转发加入战场动作的**加入触发端** → 共享处理端 → unit.joined；不扣费、不走部署词条）；
/// ⑤单位化触发器（部署/加入共用：加单位组件＋指挥组件＋实际加入空槽位＋建立修饰机制初始快照〔W2b〕；
///   S9：唯一调用者＝加入战场动作处理端——经 <see cref="InvokeUnitizeAsync"/>）。
/// 门户（W2b G3；单位数值受控变更面——防御语义）：伤害扣减 <see cref="ApplyDefenseDamageAsync"/>（损伤量增加）与
/// 修复 <see cref="RepairDefenseAsync"/>（恢复到上限）＝运行期数值本体的合规变更入口（配合卡侧修饰容器＝修饰加值/撤销）；
/// 变更一律经「门户 → 跑链（修饰机制管线）→ 有变更集中触发」。
/// 门户（S9 G13；类型增补）：<see cref="AddUnitTypeAsync"/>＝单位类型集合的唯一合规运行期增补路径
/// （去重幂等/非法值拒绝/变更信号——实际改变集合恰发一次 unit.types.changed）。
/// S1（老兵机制）：老兵触发器（<see cref="VeteranTrigger"/>——升级动作的唯一标准发动入口；触发它＝
/// 全清换新＋广播 unit.upgraded）＋升级门户（内部实现支撑）＋公共发动包装面
/// （<see cref="InvokeVeteranTriggerAsync"/>——效果层/S3 对接路径；须经触发器、不得绕过）。
/// S2（隐蔽机制）：揭示触发器（<see cref="RevealTrigger"/>——揭示内容的承载面；「揭示：X」经效果预制体
/// 按名注入；触发它＝执行揭示内容）＋内部导航面（<see cref="InvokeRevealTriggerAsync"/> /
/// <see cref="EmitUnitRevealedAsync"/>——由 CovertRules.RevealAsync 服务统一调用、不对外）。
/// 触发数据约定：Card＝本卡、Player＝所有者（可缺省/可空——加入路径不要求归属）、Position＝目标槽位（Slot 对象）。
/// 加载模板与其余装配沿用基类（<see cref="CardBase"/>）；持久化重建经基类扩展点。
/// </summary>
public class UnitCard : CardBase
{
    private readonly LogicEngine _chainEngine;

    /// <summary>创建单位卡（对战数据组件＋触发器与默认链事件在构造期装配——实例化后即可读、可驱动；
    /// 费用校验触发器按名绑定费用检查判定器——解析器缺省＝内置默认〔独立构造即可用〕）。</summary>
    /// <param name="engine">引擎（发射/触发）。</param>
    /// <param name="definition">卡牌定义。</param>
    /// <param name="validationJudicatorResolver">验证判定器解析器（按名解析——对局路径＝注册表解析；
    /// 缺省＝null＝独立构造路径——内置默认解析）。</param>
    /// <exception cref="ArgumentNullException">engine 或 definition 为 null。</exception>
    public UnitCard(
        LogicEngine engine,
        CardDefinition definition,
        Func<string, JudicatorBinding>? validationJudicatorResolver = null)
        : base(engine, definition)
    {
        _chainEngine = engine;

        // 对战数据组件由基类构造期组件 loader 装配（battleStats 组件定义驱动——P4；单位卡组件集含该定义）。

        // 触发器（预打出/打出＝费用校验触发器——合法性验证承载；部署/加入/单位化＝链触发器）。
        PrePlayTrigger = new CostCheckTrigger("预打出触发器", this, validationJudicatorResolver);
        PlayTrigger = new CostCheckTrigger("打出触发器", this, validationJudicatorResolver);
        DeployTrigger = new Trigger<CardTriggerView>("部署触发器");
        DeployKeywordTrigger = new Trigger<CardTriggerView>("部署词条触发器");
        JoinTrigger = new Trigger<CardTriggerView>("加入触发器");
        UnitizeTrigger = new Trigger<CardTriggerView>("单位化触发器");
        // S1：老兵触发器（升级动作的唯一标准发动入口——「专属触发器」；触发它＝发动升级）。
        VeteranTrigger = new Trigger<CardTriggerView>(VeteranRules.TriggerName);
        // S2：揭示触发器（揭示动作的内容承载面——「揭示：X」内容经效果预制体按名注入；触发它＝执行揭示内容，
        // 由 CovertRules.RevealAsync 统一发动；无注入＝空转、无副作用）。
        RevealTrigger = new Trigger<CardTriggerView>(CovertRules.TriggerName);

        // S5：具名触发器登记（供效果预制体 injects 声明按名解析注入目标）。
        RegisterNamedTrigger("打出触发器", PlayTrigger, typeof(CardTriggerView));
        RegisterNamedTrigger("部署触发器", DeployTrigger, typeof(CardTriggerView));
        RegisterNamedTrigger("部署词条触发器", DeployKeywordTrigger, typeof(CardTriggerView));
        RegisterNamedTrigger("加入触发器", JoinTrigger, typeof(CardTriggerView));
        RegisterNamedTrigger("单位化触发器", UnitizeTrigger, typeof(CardTriggerView));
        RegisterNamedTrigger(VeteranRules.TriggerName, VeteranTrigger, typeof(CardTriggerView));
        RegisterNamedTrigger(CovertRules.TriggerName, RevealTrigger, typeof(CardTriggerView));

        // 默认链事件（构造期装配；装配方扩展事件按注册序追加于其后——闪击等词条注入属 2C）。
        // R9：内置链 handler 归因于本卡（registrant: this）——进入 handler 时帧触发者＝本卡，
        // 链内发信号以 view.TriggerCard 为 Source（卡自身注册 ⇒ 恒为本卡）。
        // S9：部署/加入的**执行**已下沉到加入战场动作（`JoinBattlefieldAction`）——本卡只保留入口收尾
        // （unit.deployed／unit.joined、扣费、离手、OnDeployChainFinalized）。
        PlayTrigger.Register("打出链", HandlePlayChainAsync, registrant: this);
        DeployTrigger.Register("部署链", HandleDeployChainAsync, registrant: this);
        JoinTrigger.Register("加入链", HandleJoinChainAsync, registrant: this);
        UnitizeTrigger.Register("单位化", HandleUnitizeAsync, registrant: this);
        VeteranTrigger.Register("升级链", HandleVeteranChainAsync, registrant: this);
    }

    /// <summary>预打出触发器（费用校验：指挥点验证；开始/交互编排由打出管理器驱动；装配方扩展点保留）。</summary>
    public Trigger<CardTriggerView> PrePlayTrigger { get; }

    /// <summary>打出触发器（费用校验＋默认链：外层复验＋打出宣告＋部署链＋收尾〔扣费→离手〕）。</summary>
    public Trigger<CardTriggerView> PlayTrigger { get; }

    /// <summary>部署触发器（部署链：部署逻辑检查＋词条效果按序 → 单位化 → unit.deployed）。</summary>
    public Trigger<CardTriggerView> DeployTrigger { get; }

    /// <summary>加入触发器（加入链：单位化 → unit.joined；不扣费、不走部署词条）。</summary>
    public Trigger<CardTriggerView> JoinTrigger { get; }

    /// <summary>单位化触发器（部署/加入共用：加单位组件＋指挥组件＋实际加入空槽位）。</summary>
    public Trigger<CardTriggerView> UnitizeTrigger { get; }

    /// <summary>
    /// 老兵触发器（S1；升级动作的**唯一标准发动入口**——「专属触发器」）：触发它＝发动升级
    /// （全清换新＋广播 <c>unit.upgraded</c>）。默认链＝升级执行（内部门户）；由卡牌的老兵触发效果
    /// （自托管）在条件成立时经公共面触发（<see cref="InvokeVeteranTriggerAsync"/>——效果层/S3 统一路径）。
    /// 标识名＝<see cref="VeteranRules.TriggerName"/>（S1 定稿——对 S3 契约；并已具名登记，
    /// 供效果预制体 <c>injects</c> 按名解析）。
    /// </summary>
    public Trigger<CardTriggerView> VeteranTrigger { get; }

    /// <summary>
    /// 揭示触发器（S2；揭示动作的**内容承载面**——「揭示：X」内容经效果预制体 <c>injects</c> 按名注入；
    /// 触发它＝执行揭示内容）。默认链＝无内置逻辑（未注入 handler＝空转、无副作用——本人无「揭示：」内容
    /// 即此态；信号照发）。揭示动作的**唯一标准发动入口**＝<see cref="CovertRules.RevealAsync"/>
    /// （先移除隐蔽标记 → 触发本触发器 → 广播 <c>unit.revealed</c>；不得绕过直调内部步骤）。
    /// 标识名＝<see cref="CovertRules.TriggerName"/>（S2 定稿——对 S3 契约；并已具名登记，
    /// 供效果预制体 <c>injects</c> 按名解析）。
    /// </summary>
    public Trigger<CardTriggerView> RevealTrigger { get; }

    /// <summary>
    /// 部署词条触发器（S6／R12；默认 band）：部署时执行的"部署词条效果"的触发面——
    /// 部署效果经效果系统表达（效果预制体声明 <c>inject.target</c>＝本触发器名，装载时由框架侧注入 handler）；
    /// 部署链①段与再触发「部署重放」共用 <see cref="InvokeDeployKeywordAsync"/> 单一入口。
    /// 无注入＝空转（部署不因此失败）。
    /// </summary>
    public Trigger<CardTriggerView> DeployKeywordTrigger { get; }

    /// <summary>
    /// 触发部署词条效果（S6 单源入口）：部署链与「部署重放」共用；载荷＝{ Card, Player?, Position }。
    /// </summary>
    internal async Task<EventStream> InvokeDeployKeywordAsync(Player? player, Slot slot, CancellationToken ct)
    {
        var result=await DeployKeywordTrigger.InvokeAsync(_chainEngine, BuildUnitData(this, player, slot), ct);
        if(result.Outcome==ExecutionOutcome.Normal)await KeywordProgramRuntime.ReplayDeployment(this,ct);
        return result;
    }

    /// <summary>
    /// 手牌起始指向槽位声明（S2：由单位卡自行声明——基类不持 targeter）：单位＝手牌打出时指向空槽
    /// （候选域由打出路径填充）；槽位参数＝本卡（起始卡牌）。声明固定、随实例复用。
    /// </summary>
    public IReadOnlyList<Selector> HandOriginSlots { get; } =
        new Selector[] { SelectorTemplates.UnitHandDrag };

    // ---------- 默认链（构造期装配；链只经公开触发器面驱动） ----------

    /// <summary>
    /// 打出链（外层；仅部署路径）：打出宣告由**加入战场动作**的部署触发端 <c>Announce</c> 段发射；
    /// 本 handler 只保留**入口收尾**——<c>unit.deployed</c> → 扣部署费 → 离手 → 词条落点。
    /// </summary>
    private async Task HandlePlayChainAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not UnitCard unit || view.Player is not Player player)
        {
            ctx.Interrupt(); // 载荷缺失（结构性错误）：链中止、收尾不发生
            return;
        }

        // A.20：阵型空隙（FormationGap）＝部署主入口的落点；提交的是"空隙身份"而非物理空槽。
        var slot = view.Position;
        if (view.FormationGap is { } gap)
        {
            if (!gap.Line.TryOpenGap(gap, out slot)) { ctx.Interrupt(); return; }
        }

        if (slot is null) { ctx.Interrupt(); return; }

        // 内层部署链（部署词条 → 单位化 → card.placed）——S9：执行全部下沉到「加入战场」动作的部署触发端。
        await unit.DeployTrigger.InvokeAsync(_chainEngine, BuildUnitData(unit, player, slot), ct);
        if (ctx.Interrupted)
        {
            return; // 部署链未完成（代价不足/结构拒绝）：不进入收尾（不扣费、不离手、不发 unit.deployed）
        }

        // 部署完成：unit.deployed（单位化之后——组件挂载＋槽位占用已成立；R9：Source＝本卡）→
        // 扣费（仅部署扣费——恰一次；读有效部署费——与校验/复验同口径）→ 离手 → 词条落点（钳击＝同伴选择）。
        await GameUpdates.EmitUnitDeployed(_chainEngine, unit, slot, ct, view.TriggerCard ?? unit);

        var deployCost = unit.Modifiers.GetEffectiveValue(CardStatFields.DeployCost);
        if (ResourceManager.ResolveFor(unit) is { } manager)
        {
            await manager.ChangePointsAsync(player, -deployCost, PointChangeKind.Add, ct).ConfigureAwait(false);
        }
        else
        {
            player.Points -= deployCost; // 脱局兜底（未装配资源管理器）：保持既有直写语义
        }

        player.Hand.Remove(unit);
        await KeywordRules.OnDeployChainFinalizedAsync(unit, ct);
    }

    /// <summary>
    /// 部署链（内层；S9）：转发到**加入战场动作**的部署触发端——代价校验 → <c>card.played</c> →
    /// 部署词条 → 共享处理端（单位化 + 落位 + <c>card.placed</c>）。本卡不再自持执行段。
    /// </summary>
    private async Task HandleDeployChainAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not UnitCard unit || view.Position is not { } slot || view.Player is not Player player)
        {
            ctx.Interrupt();
            return;
        }

        var action = ActionRuntime.ResolveJoinBattlefield(_chainEngine);
        if (action is null)
        {
            // 装配性错误（独立构造的卡无对局动作集）：链中止、零落地、留痕。
            _chainEngine.RootStream.WriteLog(
                "部署链", $"单位 '{unit.Name}' 的部署被拒绝：本引擎未装配加入战场动作。",
                LogLevel.Error, new[] { "action:missing" });
            ctx.Interrupt();
            return;
        }

        await using var scope = _chainEngine.BeginAction();
        var result = await action.DeployAsync(unit, slot, player, view.TriggerCard, ct)
            .ConfigureAwait(false);
        if (result.Targets[0].Outcome != ActionOutcomeKind.Applied)
        {
            ctx.Interrupt(); // 处理端未落地：不发射 card.placed／unit.deployed，入口收尾不发生
        }
    }

    /// <summary>加入链（S9）：转发到加入战场动作的加入触发端 → 共享处理端；完成后发 <c>unit.joined</c>。</summary>
    private async Task HandleJoinChainAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not UnitCard unit || view.Position is not { } slot)
        {
            ctx.Interrupt();
            return;
        }

        var action = ActionRuntime.ResolveJoinBattlefield(_chainEngine);
        if (action is null)
        {
            _chainEngine.RootStream.WriteLog(
                "加入链", $"单位 '{unit.Name}' 的加入被拒绝：本引擎未装配加入战场动作。",
                LogLevel.Error, new[] { "action:missing" });
            ctx.Interrupt();
            return;
        }

        await using var scope = _chainEngine.BeginAction();
        var result = await action.JoinAsync(unit, slot, view.TriggerCard, ct).ConfigureAwait(false);
        if (result.Targets[0].Outcome != ActionOutcomeKind.Applied)
        {
            ctx.Interrupt(); // 处理端未落地：不发射 card.placed／unit.joined
            return;
        }

        await GameUpdates.EmitUnitJoined(_chainEngine, unit, slot, ct, view.TriggerCard ?? unit);
    }

    /// <summary>
    /// 单位化（部署/加入共用；链尾共用段，S9 起由加入战场动作处理端经
    /// <see cref="IJoinActionServices.UnitizeOnJoinAsync"/> 单源调用）：加单位组件（位置＝槽位、已毁＝false、
    /// 类型＝从定义填充）＋指挥组件（初始 false/false）＋实际加入空槽位＋请求跑链一次（建立修饰机制「初始快照」）。
    /// 结构不变量（矛盾中间态防护）：仅空槽、未单位化的卡可单位化——违反＝链中止（零中间态残留）。
    /// </summary>
    private async Task HandleUnitizeAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not UnitCard unit || view.Position is not { } slot
            || !slot.IsEmpty || unit.TryGetData<UnitStateData>(out _))
        {
            ctx.Interrupt(); // 载荷缺失 / 槽位非空 / 二次单位化：链中止（留痕由 Interrupt 写入）
            return;
        }

        var state = UnitStateData.CreateInitial(unit.GetData<BattleStatsData>());
        state.FillInitialTypes(unit.Definition.UnitTypes); // 单位类型清单从定义填充（部署/加入两路径一致）
        state.Position = slot;
        unit.AddData(state);
        unit.AddData(new CommandData());
        unit.AddData(new ActionRecordData());
        slot.Place(unit);

        // W2b G3 接线：装配完成点请求跑链一次（首轮——以基准状态建立比较基线；本时点无修饰/无损伤＝零变化、零发射）。
        await unit.Modifiers.RequestRerunAsync(ct);
    }

    /// <summary>
    /// 单位化的**卡侧单源入口**（S9；唯一调用者＝加入战场动作服务面）：触发「单位化触发器」
    /// （载荷＝{ Card, Position }），由默认链执行单位化；注入该触发器的效果照常参与。
    /// </summary>
    internal Task InvokeUnitizeAsync(Slot slot, CancellationToken ct = default)
        => UnitizeTrigger.InvokeAsync(
            _chainEngine,
            new Dictionary<string, object?>
            {
                [GameUpdates.PayloadCard] = this,
                [GameUpdates.PayloadPosition] = slot,
            },
            ct);

    // ---------- S2 隐蔽机制（揭示导航面——由 CovertRules.RevealAsync 服务调用、不对外） ----------

    /// <summary>
    /// 发动揭示内容（S2；内部导航——由 <see cref="CovertRules.RevealAsync"/> 服务调用；揭示动作的外部路径
    /// 须经 RevealAsync、不得绕过直调内部步骤）：触发「揭示触发器」（载荷＝{ Card }——对齐老兵触发器先例）。
    /// 未注入 handler＝空转（返回空流、无副作用——本人无「揭示：」内容即此态）。
    /// </summary>
    internal Task InvokeRevealTriggerAsync(CancellationToken ct = default)
        => RevealTrigger.InvokeAsync(
            _chainEngine,
            new Dictionary<string, object?> { [GameUpdates.PayloadCard] = this },
            ct);

    /// <summary>
    /// 发射揭示信号（S2；内部导航——由 <see cref="CovertRules.RevealAsync"/> 服务在揭示落定后调用、
    /// 恰一次：<c>unit.revealed</c>——先落定后发射；观察者所见即终态）。
    /// </summary>
    internal Task EmitUnitRevealedAsync(CancellationToken ct = default)
        => GameUpdates.EmitUnitRevealed(_chainEngine, this, ct);

    // ---------- S1 老兵机制（升级发动面与执行门户） ----------

    /// <summary>
    /// 老兵版本定义查找提供器（S1；加载路径注入——对局卡牌库）：老兵卡 id → 定义（未注册/无提供器＝null）。
    /// 升级执行「查找老兵版本定义」的读取来源；独立构造（不经卡牌库）＝null（无法解析——归拒绝路径）。
    /// </summary>
    internal Func<string, CardDefinition?>? VeteranDefinitionLookup { get; set; }

    /// <summary>
    /// 定义 → 注册 id 反查提供器（S1；加载路径注入——对局卡牌库）：当前卡定义的注册 id（未注册/无提供器＝null）。
    /// 升级执行「来源指向当前卡」校验的读取来源；独立构造（不经卡牌库）＝null（无法核对——归拒绝路径）。
    /// </summary>
    internal Func<CardDefinition, string?>? DefinitionRegisteredIdLookup { get; set; }

    /// <summary>
    /// 发动升级（S1；**经「老兵触发器」**——唯一标准发动入口的公共包装面）：触发「老兵触发器」并回传三态结果
    /// （捕获箱通道）。效果层（csx——经 <c>EffectRuntime.UpgradeAsync</c>）与测试（机制级全链）统一的发动路径。
    /// 语义：成功升级＝形态切换＋信号恰一次（有变更时 <c>card.stat.changed</c> 先、<c>unit.upgraded</c> 后）；
    /// 幂等（已是老兵）/拒绝＝零副作用、零信号（三态经返回结果程序化区分）；执行段内失败＝回滚至原状＋异常上抛
    /// （原异常重抛——「异常上抛（可辨识）」外部形态；触发器内部照常隔离记录）。
    /// </summary>
    /// <exception cref="InvalidOperationException">触发器未提交结果（内部错误——契约违反）。</exception>
    public async Task<VeteranPromotionResult> InvokeVeteranTriggerAsync(CancellationToken ct = default)
    {
        var capture = new VeteranPromotionCapture();
        var data = new Dictionary<string, object?>
        {
            [GameUpdates.PayloadCard] = this,
            [VeteranPromotionCapture.PayloadKey] = capture,
        };

        await VeteranTrigger.InvokeAsync(_chainEngine, data, ct).ConfigureAwait(false);

        if (capture.Failure is { } failure)
        {
            // 执行段失败：回滚已完成、异常上抛（发动方视角——原异常重抛、保留堆栈）。
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return capture.Result
            ?? throw new InvalidOperationException(
                $"「老兵触发器」执行未提交结果（内部错误——handler 契约违反；单位 '{Name}'）。");
    }

    /// <summary>
    /// 升级链（「老兵触发器」默认链；S1）：解析载荷 → 调用升级门户（内部实现）→ 结果写入捕获箱（如有）。
    /// 执行段失败（门户回滚后上抛）：写入捕获箱失败通道（发动方重抛）；本处不再上抛——触发器事件级
    /// 隔离语义照常（不引入额外噪音）。取消类异常＝穿透上抛（框架既有语义）。
    /// </summary>
    private async Task HandleVeteranChainAsync(CardTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Card is not UnitCard unit)
        {
            ctx.Interrupt(); // 载荷缺失（结构性错误）：链中止
            return;
        }

        try
        {
            var result = await unit.PromoteToVeteranAsync(ct).ConfigureAwait(false);
            if (ctx.TryGet(VeteranPromotionCapture.PayloadKey, out var value)
                && value is VeteranPromotionCapture capture)
            {
                capture.Capture(result);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 取消类异常不隔离、穿透上抛（框架既有语义）
        }
        catch (Exception ex)
        {
            if (ctx.TryGet(VeteranPromotionCapture.PayloadKey, out var value)
                && value is VeteranPromotionCapture capture)
            {
                capture.CaptureFailure(ex);
            }
        }
    }

    /// <summary>
    /// 升级门户（S1 内部实现支撑——「老兵触发器」默认链的落点；效果层不直接调用本方法——须经触发器发动）。
    /// 流程＝预检（全部前置条件先于任何副作用校验；可预检失败＝拒绝、零副作用）
    /// → 执行段（延迟窗口内＝对外零中间发射：清损伤＋修饰器全清＋词条集全清 → 按老兵版本换成新数值基准
    ///   ＋受控刷新攻/行动费实时值 → 授予老兵版本词条集；执行段内失败＝回滚至升级前完整原状＋异常上抛）
    /// → 收尾（单次重算——有变更恰一次 <c>card.stat.changed</c>；随后广播 <c>unit.upgraded</c> 恰一次）。
    /// 三态：成功升级（Promoted）/ 幂等无操作（已是老兵——零副作用零信号）/ 拒绝（零副作用零信号）。
    /// 替换范围＝仅「战斗数值（攻/防/行动费）＋词条集」；范围外（定义引用/类型集/部署费/名称等）保持基础版本不变；
    /// 操作与状态（位置/指挥/行动状态/在场回合数等）不因升级重置（除本次明确清空/替换者外全部保持）。
    /// </summary>
    internal async Task<VeteranPromotionResult> PromoteToVeteranAsync(CancellationToken ct = default)
    {
        // ---------- 预检（全部前置条件先于任何副作用；可预检失败＝拒绝、零副作用） ----------

        // ① 在场且存活（未单位化未入场/已死亡已毁/已离场＝拒绝——「在场且存活」谓词不满足即拒绝）。
        if (!TryGetData<UnitStateData>(out var state) || state.IsDestroyed || state.Position is null)
        {
            return VeteranPromotionResult.Rejected(VeteranPromotionRejectionReason.NotOnField);
        }

        // ② 幂等（已是老兵形态＝无操作：零副作用、零信号、不中断触发链——天然断开「升级→监听→再升级」重入环）。
        if (VeteranRules.IsVeteran(this))
        {
            return VeteranPromotionResult.AlreadyVeteran();
        }

        // ③ 老兵版本可解析（未声明/目标缺失/目标不完整/来源错配——均归「无老兵版本」拒绝、原因类别可读）。
        var veteranId = Definition.BecomesVeteran;
        if (string.IsNullOrWhiteSpace(veteranId))
        {
            return VeteranPromotionResult.Rejected(VeteranPromotionRejectionReason.VersionNotDeclared);
        }

        var currentId = DefinitionRegisteredIdLookup?.Invoke(Definition);
        if (string.IsNullOrWhiteSpace(currentId)
            || VeteranDefinitionLookup?.Invoke(veteranId) is not { } veteranDefinition)
        {
            return VeteranPromotionResult.Rejected(VeteranPromotionRejectionReason.VersionMissing);
        }

        if (string.IsNullOrWhiteSpace(veteranDefinition.VeteranOf))
        {
            return VeteranPromotionResult.Rejected(VeteranPromotionRejectionReason.VersionIncomplete);
        }

        if (!string.Equals(veteranDefinition.VeteranOf, currentId, StringComparison.Ordinal))
        {
            // 最小硬校验：目标定义存在 ∧ 目标定义的来源指向当前卡（不一致＝数据错配防护）。
            return VeteranPromotionResult.Rejected(VeteranPromotionRejectionReason.VersionSourceMismatch);
        }

        // ④ 待授词条集可构造（防御校验——映射路径下已保证注册；覆盖代码注册路径的意外）。
        foreach (var declaration in veteranDefinition.Keywords)
        {
            if (!KeywordRegistry.IsDefined(declaration.Id))
            {
                return VeteranPromotionResult.Rejected(VeteranPromotionRejectionReason.KeywordSetUnconstructable);
            }
        }

        // ---------- 快照（回滚目标＝升级前完整原状） ----------

        var modifierSnapshot = Modifiers.All.ToArray();
        var keywordSnapshot = Keywords.Components.Select(component => (component.Keyword, component.Value)).ToArray();
        var battleStats = GetData<BattleStatsData>();
        var statsBaseline = (battleStats.OperateCost, battleStats.Attack, battleStats.Defense);
        var stateSnapshot = (state.OperateCost, state.Attack, state.Defense);
        var defenseLossSnapshot = state.DefenseLoss;

        // ---------- 执行段（延迟窗口内；自动衔接被挂起——对外零中间发射） ----------

        var granted = new List<string>();
        using (Modifiers.BeginRerunDeferral())
        {
            try
            {
                // ① 词条集全清（含运行时授予——「无保留位」全清；逐条完整卸载、异常隔离）。
                await Keywords.ClearAllAsync(ct).ConfigureAwait(false);

                // ② 修饰器全清（含永久型——清空处置同款原语；撤销的自动衔接被延迟窗口挂起）。
                await Modifiers.ClearAllAsync(ct).ConfigureAwait(false);

                // ③ 清损伤（恢复到上限——「升级即满值」；跑链被窗口挂起）。
                await RepairDefenseAsync(ct).ConfigureAwait(false);

                // ④ 换新：战斗数值基准替换（受控写面——只读基准的本体替换；强度来源＝老兵定义）。
                battleStats.ReplaceCore(
                    veteranDefinition.OperateCost, veteranDefinition.Attack, veteranDefinition.Defense);

                // ⑤ 受控刷新攻/行动费实时值（勘验陷阱①：两值链起点读 UnitStateData——仅换基准不达标）。
                //    防御实时值（表现位）由收尾单次重跑的落定同步写入（链起点＝新基准−0＝满值）。
                state.OperateCost = veteranDefinition.OperateCost;
                state.Attack = veteranDefinition.Attack;

                // ⑥ 授予老兵版本词条集（含「老兵」标记——随内容落地、单一真源；授予链 fail-fast）。
                foreach (var declaration in veteranDefinition.Keywords)
                {
                    if (await Keywords.GrantAsync(declaration.Id, declaration.Value).ConfigureAwait(false))
                    {
                        granted.Add(declaration.Id);
                    }
                }
            }
            catch
            {
                // 执行段内失败（不可预检的意外——如授予链装载失败）：回滚至升级前完整原状（力求完成）＋异常上抛。
                await RollbackPromotionAsync(
                    granted, keywordSnapshot, modifierSnapshot, state,
                    statsBaseline, stateSnapshot, defenseLossSnapshot, ct)
                    .ConfigureAwait(false);
                throw;
            }
        }

        // ---------- 收尾（先落定、后发射；恰一次） ----------

        // 单次重算：全部数值处置合并为一次重跑、一次集中触发（有变更恰一次 card.stat.changed、无变更零发射）。
        await Modifiers.RequestRerunAsync(ct).ConfigureAwait(false);

        // 机制宣告：unit.upgraded（恰一次——升级落定后；观察者所见即终态：形态＋数值均就绪）。
        await GameUpdates.EmitUnitUpgraded(_chainEngine, this, ct).ConfigureAwait(false);

        return VeteranPromotionResult.Promoted();
    }

    /// <summary>
    /// 升级执行段回滚（S1；失败路径——恢复升级前完整原状；力求完成：单步异常隔离记录、不掩盖原始失败）。
    /// 逆序恢复：已授予新词条撤销 → 原词条快照重授 → 原修饰器快照重挂 → 数值基准/实时值/损伤还原。
    /// 回滚期间仍处延迟窗口内（自动衔接被挂起）；本方法不跑链、不发射（零信号）。
    /// </summary>
    private async Task RollbackPromotionAsync(
        List<string> granted,
        (string Keyword, int? Value)[] keywordSnapshot,
        Modifier[] modifierSnapshot,
        UnitStateData state,
        (int OperateCost, int Attack, int Defense) statsBaseline,
        (int OperateCost, int Attack, int Defense) stateSnapshot,
        int defenseLossSnapshot,
        CancellationToken ct)
    {
        // ① 撤销本次已授予的新词条（逐条完整卸载；异常隔离）。
        foreach (var keyword in granted)
        {
            try
            {
                await Keywords.RevokeAsync(keyword).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WriteRollbackError($"已授予词条 '{keyword}' 撤销", ex);
            }
        }

        // ② 恢复原词条快照（重授——存在性/参值/行为装载恢复；异常隔离）。
        foreach (var (keyword, value) in keywordSnapshot)
        {
            try
            {
                await Keywords.GrantAsync(keyword, value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WriteRollbackError($"原词条 '{keyword}' 恢复", ex);
            }
        }

        // ③ 恢复原修饰器快照（卸载后为未挂载态、可重挂；异常隔离；衔接被窗口挂起）。
        try
        {
            await Modifiers.AddModifiersAsync(modifierSnapshot, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteRollbackError("原修饰器恢复", ex);
        }

        // ④ 数值还原（基准本体＋实时值/损伤——恢复升级前完整原状）。
        GetData<BattleStatsData>().ReplaceCore(
            statsBaseline.OperateCost, statsBaseline.Attack, statsBaseline.Defense);
        state.OperateCost = stateSnapshot.OperateCost;
        state.Attack = stateSnapshot.Attack;
        state.Defense = stateSnapshot.Defense;
        state.DefenseLoss = defenseLossSnapshot;
    }

    /// <summary>升级回滚单步失败留痕（引擎总流；隔离记录——不掩盖原始失败）。</summary>
    private void WriteRollbackError(string step, Exception ex)
    {
        _chainEngine.RootStream.WriteLog(
            "老兵升级回滚",
            $"单位 '{Name}'：{step}异常（隔离：继续完成回滚）——{ex.Message}",
            LogLevel.Error,
            new[] { "veteran", "rollback", $"exception:{ex.GetType().Name}" },
            new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            });
    }

    // ---------- 门户（W2b G3；单位数值受控变更面——防御语义） ----------

    /// <summary>
    /// 门户：防御伤害扣减（受控变更——数值本体的运行期唯一合规入口之一；配合修饰容器＝修饰加值/撤销）：
    /// 损伤量增加（伤害＝即时变更：只扣当前、不减上限；不经修饰器）→ 跑链（修饰机制管线）→ 有变更才发
    /// （card.stat.changed——先落定、后发射）；返回即终态（致死时含死亡判定与死亡流程完成）。
    /// 死亡后数值面冻结：已死亡/已毁＝明确拒绝（fail-fast、零副作用）；未单位化＝明确异常（装配性错误）。
    /// 批 1（动员效果化）：动员「受伤失去」由动员效果监听本门户发射的 card.damaged 信号承载——
    /// 门户不再感知动员（直调路径退役）；信号发射条件不变（净伤害＞0 且防御实际变化；先落定后发射）。
    /// <para>R11 起为 <c>internal</c> 受控面：**唯一**调用方＝伤害动作服务面
    /// （<c>DamageAction</c> 处理端 → <c>MatchActionServices.ApplyDamageAsync</c>）
    /// ——其他程序集编译期不可调（测试程序集为友元）。</para>
    /// </summary>
    /// <param name="amount">伤害量（≥0；0＝合法——跑链无变化时零发射）。</param>
    /// <exception cref="ArgumentOutOfRangeException">amount 为负。</exception>
    /// <exception cref="InvalidOperationException">未单位化；或已死亡/已毁（死亡后数值面冻结——明确拒绝）。</exception>
    internal async Task ApplyDefenseDamageAsync(int amount, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var state = RequireUnitState();
        if (state.IsDestroyed)
        {
            throw new InvalidOperationException(
                $"单位 '{Name}' 已死亡/已毁，不能进行伤害操作（死亡后数值面冻结——明确拒绝）。");
        }

        var defenseBefore = Modifiers.GetEffectiveValue(CardStatFields.Defense); // E1-33：受伤害信号按「改变才传播」
        state.DefenseLoss += amount; // 受控写入（门户面；运行期直写收窄）

        // 批 1（动员效果化）：门户直调路径退役——「受到伤害后失去」不再由门户感知，改由动员效果
        // （MobilizeLossEffect——批 4 数据化行为引用目标）监听下方 card.damaged 信号自我撤销（净伤害＞0＝防御实际扣减；被完全吸收/
        // 归零〔amount=0〕不发信号＝不算；失去走词条移除链、既得 +1/+1 保留）。先记伤害、后跑链、末发信号。
        await Modifiers.RequestRerunAsync(ct); // 变更经门户 → 跑链 → 变化时集中触发

        if (amount > 0 && Modifiers.GetEffectiveValue(CardStatFields.Defense) != defenseBefore)
        {
            await GameUpdates.EmitCardDamaged(_chainEngine, this, amount, ct); // E1-33：**防御实际变化**才发（改变才传播）
        }
    }

    /// <summary>
    /// 门户：修复（受控变更——恢复到上限）：损伤量清零（＝当前值恢复到有效上限）→ 跑链 → 有变更才发。
    /// 仅对存活单位有效：已死亡/已毁＝明确拒绝（fail-fast）；满血（无损伤）＝幂等无变化（零发射）。
    /// （G17 完整语义后续批次；本单＝动作面＋模拟验证。）
    /// </summary>
    /// <exception cref="InvalidOperationException">未单位化；或已死亡/已毁（明确拒绝）。</exception>
    public async Task RepairDefenseAsync(CancellationToken ct = default)
    {
        var state = RequireUnitState();
        if (state.IsDestroyed)
        {
            throw new InvalidOperationException(
                $"单位 '{Name}' 已死亡/已毁，不能修复（仅对存活单位有效——明确拒绝）。");
        }

        state.DefenseLoss = 0;
        await Modifiers.RequestRerunAsync(ct);
    }

    /// <summary>
    /// 门户：定量修复（受控变更——损伤量减少 <paramref name="amount"/>，下限 0）：跑链 → 有变更才发。
    /// 仅对存活单位有效；<paramref name="amount"/>＝0 或满血＝幂等无变化（零发射）。
    /// </summary>
    /// <param name="amount">修复量（非负；超出当前损伤量时按清零处理）。</param>
    /// <exception cref="ArgumentOutOfRangeException">amount 为负。</exception>
    /// <exception cref="InvalidOperationException">未单位化；或已死亡/已毁（明确拒绝）。</exception>
    public async Task RepairDefenseAsync(int amount, CancellationToken ct = default)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "修复量不能为负。");
        }

        var state = RequireUnitState();
        if (state.IsDestroyed)
        {
            throw new InvalidOperationException(
                $"单位 '{Name}' 已死亡/已毁，不能修复（仅对存活单位有效——明确拒绝）。");
        }

        if (amount == 0 || state.DefenseLoss == 0)
        {
            return;
        }

        state.DefenseLoss = Math.Max(0, state.DefenseLoss - amount);
        await Modifiers.RequestRerunAsync(ct);
    }

    /// <summary>
    /// 门户：设定剩余防御（受控变更——按有效上限折算损伤量；含致死超额回复）：跑链 → 有变更才发。
    /// </summary>
    /// <param name="amount">目标剩余防御（须 ≥ 1）。</param>
    /// <exception cref="ArgumentOutOfRangeException">amount 小于 1。</exception>
    /// <exception cref="InvalidOperationException">未单位化；或已死亡/已毁（明确拒绝）。</exception>
    public async Task SetRemainingDefenseAsync(int amount, CancellationToken ct = default)
    {
        if (amount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "剩余防御须至少为 1。");
        }

        var state = RequireUnitState();
        if (state.IsDestroyed)
        {
            throw new InvalidOperationException($"单位 '{Name}' 已死亡/已毁，不能设定防御。");
        }

        var previous = state.DefenseLoss;
        try
        {
            state.DefenseLoss = 0;
            var maximum = Modifiers.ComputeEffectiveValue(CardStatFields.Defense);
            state.DefenseLoss = Math.Max(0, maximum - amount);
        }
        catch
        {
            state.DefenseLoss = previous;
            throw;
        }

        await Modifiers.RequestRerunAsync(ct);
    }

    /// <summary>
    /// 门户：类型增补（S9；单位类型集合的唯一合规运行期增补路径——「也算作」类效果的受控入口）：
    /// 集合语义（「确保该类型在集合中」）——已含该类型＝幂等无操作（不重复登记、不重复发信号）；null 不可达（值类型）；
    /// 未定义枚举值＝拒绝（非法值）；未单位化（无单位数据组件）＝明确异常（装配性错误）；
    /// 实际改变集合＝恰发射一次 <see cref="GameUpdates.UnitTypesChanged"/>（载荷＝本单位＋新增类型——增量；
    /// 完整类型集由监听者从单位读取「读取现势」）。
    /// 生效即时（判定读点动态读取当前实例类型集——无需修改读点代码）；实例级（仅作用于本实例、不写回定义；
    /// 不迁移同定义其他实例）；转换产生的静态新实例不携带本增补（新实例＝从定义重建）。
    /// </summary>
    /// <param name="type">单位类型标识（须为已定义标识；非法值＝拒绝）。</param>
    /// <exception cref="ArgumentOutOfRangeException">type 为未定义标识（非法值——明确拒绝）。</exception>
    /// <exception cref="InvalidOperationException">尚未单位化（缺单位数据组件——装配性错误）。</exception>
    public async Task AddUnitTypeAsync(string type, CancellationToken ct = default)
    {
        if (!UnitType.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type), type, "未定义的单位类型标识（非法值——明确拒绝）。");
        }

        var state = RequireUnitState();
        if (!state.TryAddRuntimeType(type))
        {
            return; // 幂等（已含该类型）：无操作、不重复发信号——集合语义「确保在集合中」、结果成功
        }

        await GameUpdates.EmitUnitTypesChanged(_chainEngine, this, type, ct); // 实际变更：恰一次（先落定、后发射）
    }

    /// <summary>
    /// 读取「防御有效上限」（只读查询面；W2b）：有效上限＝对战组件基准＋Σ加防修饰（现算；只读、不落定不发射）。
    /// 与有效值读取面并列；不提供任意写（变更仍经门户/修饰）。死亡后查询可用（数值本体保留、只读）。
    /// </summary>
    /// <exception cref="InvalidOperationException">未单位化（缺单位数据组件——基准来源未就绪）。</exception>
    public int GetEffectiveDefenseCap() => Modifiers.GetEffectiveCapValue(CardStatFields.Defense);

    // ---------- 在场回合数（G14补 S10；回合事件驱动计数） ----------

    /// <summary>
    /// 读取当前在场回合数（G14补 S10 读取面——最小读取集「读取当前在场回合数」唯一能力；
    /// 「是否处于在场第 N 回合」＝读取值比较派生、不单独立面）。
    /// 语义：入场即第 1 回合；己方回合正式开始（turn.start）递增（单方步进、静默）；
    /// 死亡后停止递增（值保持、可读）；未入场（手牌/卡组）＝不适用（null、不抛错——沿用降级先例）。
    /// </summary>
    public int? TurnsInPlay => TryGetData<UnitStateData>(out var state) ? state.TurnsInPlay : null;

    /// <summary>
    /// 在场回合数递增（G14补 S10 内部执行面；回合事件驱动——由对局在「单位归属玩家的回合正式开始」时调用；
    /// 单方步进：仅归属==回合开始方时递增——调用方负责归属过滤）：未单位化/已死亡＝false 无操作
    /// （死亡停止；死亡单位已离场、通常不可达——防御双保险）；递增＝true。
    /// 静默数据变更（不发射/不通知——不新增信号）。
    /// </summary>
    internal bool AdvanceTurnsInPlay()
    {
        if (!TryGetData<UnitStateData>(out var state) || state.IsDestroyed)
        {
            return false;
        }

        state.TurnsInPlay += 1;
        return true;
    }

    /// <summary>单位数据组件就绪校验（门户操作前提；未单位化＝明确异常、不静默）。</summary>
    private UnitStateData RequireUnitState()
        => TryGetData<UnitStateData>(out var state)
            ? state
            : throw new InvalidOperationException(
                $"单位 '{Name}' 尚未单位化（缺单位数据组件），不能执行数值变更操作（装配性错误）。");

    /// <summary>单位链触发数据（Card / Player / Position；Player 可缺省/可空——加入路径不要求归属）。</summary>
    private static Dictionary<string, object?> BuildUnitData(UnitCard unit, Player? player, Slot slot)
        => new()
        {
            [GameUpdates.PayloadCard] = unit,
            [GameUpdates.PayloadPlayer] = player,
            [GameUpdates.PayloadPosition] = slot,
        };
}
