using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Managers;
using Orc.Game.Targeting;

namespace Orc.Game.Players;

/// <summary>
/// 玩家（对局数据类；本批不继承引擎 Entity——无引用/生命周期需求，实体化留后续批次评估）：
/// 资源（指挥点数 / 指挥点槽）＋ 卡组（名单）＋ 手牌（实例集）＋ HQ（总部实体）＋ 卡组构筑配置（主国/盟国——S10 加性面）。
/// 读面直接可读（供测试断言与展示）；写面经管理器（资源结算＝资源管理器；卡组消耗/手牌装载＝玩家管理器；
/// HQ 数值变更＝HQ 数值路径〔伤害经指挥管理器调用 HQ 门户〕），无旁路修改入口。
/// W3-3 G11 受控变更：HQ 由「纯数据」实体化为独立总部实体（<see cref="Hq"/>；Entity＋组件容器＋
/// 词条/效果装载面，不入死亡/销毁链）——随 Player 创建并互持（「未绑定」在结构上不可达）；
/// 占位（支援线槽 2，居中）由对局/战场装配完成（布局语义）；
/// <see cref="HqHealth"/> 为只读转发面（转发 HQ 有效血量；不构成第二真源——写入一律走 HQ 侧受控路径）；
/// 归零判定＝HQ 数值路径下游统一响应（终局：状态置结束＋胜者记录＝HQ 归零方之对手；其后动作入口拒绝）。
/// </summary>
public sealed class Player
{
    /// <summary>HQ 初始血量（20）。</summary>
    public const int InitialHqHealth = 20;

    /// <summary>
    /// 引擎托管引用键（创建期由 <see cref="Orc.Core.LogicEngine.References"/> 分配；N 格式 GUID 字符串）。
    /// 玩家不是 <see cref="Orc.Core.Entity"/>（无 <c>Id</c>），故其跨边界引用一律以本键表达。
    /// **同一对局内稳定；重建对局后变化**——不得跨对局复用。
    /// </summary>
    public string ReferenceKey { get; internal set; } = string.Empty;

    /// <summary>
    /// 手牌上限（9）。G7 起启用——回合抽牌链路超限裁决：满手时新抽的牌不经手牌、直爆（销毁＋
    /// <c>card.burned</c> 信号——KARDS 爆牌语义）；裁决点＝回合抽牌链路（起手装载不受裁决、维持静默）；
    /// 常量值与公开读面不变、超限判定为内部行为（不新增公开状态）。
    /// </summary>
    public const int HandLimit = 9;

    internal Player(int index, CardList deck, LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Index = index;
        Deck = deck;
        Hq = new Hq(this, engine); // W3-3：HQ 随 Player 创建（双向互持——未绑定状态在结构上不可达）
    }

    /// <summary>玩家索引（0＝玩家A/第一位玩家、1＝玩家B）。</summary>
    public int Index { get; }

    /// <summary>卡组（名单：剩余可抽的卡牌 id 序列；抽牌＝取首张并移除）。</summary>
    public CardList Deck { get; }

    /// <summary>手牌（卡牌实例集合）。</summary>
    public CardSet Hand { get; } = new();

    /// <summary>指挥点数（当前值；回合开始结算＝槽值〔设为〕；X3：回合结束与敌方回合内保留——不清零）。</summary>
    public int Points { get; internal set; }

    /// <summary>指挥点槽（回合开始 +1、至上限封顶；保留于回合结束）。</summary>
    public int PointSlots { get; internal set; }

    /// <summary>
    /// 总部实体（W3-3 G11：随 Player 创建并互持；Entity＋组件容器＋词条/效果装载面，不入死亡/销毁链；
    /// 占位/数值/目标承载等 HQ 面经本引用访问）。
    /// </summary>
    public Hq Hq { get; }

    /// <summary>
    /// HQ 血量（初始 20）。〔W3-3 受控变更〕转发读面：只读转发 HQ 有效血量
    /// （＝HQ 修饰机制链输出缓存——「始终读有效值」；不构成第二真源，写入走 HQ 侧受控路径）；
    /// 不依赖入槽（未入槽亦可读——初值 20）。
    /// </summary>
    public int HqHealth => Hq.Health;

    /// <summary>
    /// 游戏环境（W3-1 G4 加性面；internal）：对局装配期注入（战场创建后、卡加载前）——
    /// 「卡 → 玩家 → 对局/战场」读取路径的玩家环节（链/条件节/收集步骤经卡归属玩家的本引用取环境）；
    /// 脱局场景（未注入）＝null（无环境面——相关读取自然产出原值、不抛错）。
    /// </summary>
    internal GameEnvironment? Environment { get; private set; }

    /// <summary>
    /// 装配期注入游戏环境（W3-1 G4；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：须先于任何卡加载（效果装载注册与链读取依赖环境就绪）。
    /// </summary>
    /// <exception cref="ArgumentNullException">environment 为 null。</exception>
    /// <exception cref="InvalidOperationException">环境已注入（重复注入被拒绝）。</exception>
    internal void ConfigureEnvironment(GameEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (Environment is not null)
        {
            throw new InvalidOperationException("玩家环境已注入（重复注入被拒绝）。");
        }

        Environment = environment;
    }

    /// <summary>
    /// 对局随机服务（第 2 批 G8 加性面；internal）：对局装配期注入——「卡 → 玩家 → 服务」读取路径的玩家环节
    /// （效果运行期经 <see cref="MatchRandomService.ResolveFor"/> 取用）；脱局场景（未注入）＝null（无服务面——
    /// 相关解析自然产出 null、不抛错）。
    /// </summary>
    internal MatchRandomService? RandomService { get; private set; }

    /// <summary>
    /// 装配期注入对局随机服务（第 2 批 G8；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：与装配一致（先于卡加载；显式、可测试——无隐藏全局单例）。
    /// </summary>
    /// <exception cref="ArgumentNullException">service 为 null。</exception>
    /// <exception cref="InvalidOperationException">随机服务已注入（重复注入被拒绝）。</exception>
    internal void ConfigureRandomService(MatchRandomService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (RandomService is not null)
        {
            throw new InvalidOperationException("玩家随机服务已注入（重复注入被拒绝）。");
        }

        RandomService = service;
    }

    /// <summary>
    /// 再触发服务（A4 加性面；internal）：对局装配期注入——「卡 → 玩家 → 服务」读取路径的玩家环节
    /// （效果运行期经 <see cref="RetriggerSystem.ResolveFor"/> 取用）；
    /// 脱局场景（未注入）＝null（无服务面——解析自然产出 null、不抛错）。
    /// </summary>
    internal RetriggerSystem? RetriggerService { get; private set; }

    /// <summary>
    /// 装配期注入再触发服务（A4；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：与装配一致（先于卡加载；显式、可测试——无隐藏全局单例）。
    /// </summary>
    /// <exception cref="ArgumentNullException">service 为 null。</exception>
    /// <exception cref="InvalidOperationException">再触发服务已注入（重复注入被拒绝）。</exception>
    internal void ConfigureRetriggerService(RetriggerSystem service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (RetriggerService is not null)
        {
            throw new InvalidOperationException("玩家再触发服务已注入（重复注入被拒绝）。");
        }

        RetriggerService = service;
    }

    /// <summary>
    /// 对局卡牌服务（S9 加性面；internal）：对局装配期注入——「卡 → 玩家 → 服务」读取路径的玩家环节
    /// （效果运行期经 <see cref="MatchCardService.ResolveFor"/> 取用——生成/复制/转换的「卡牌工厂＋放置面」）；
    /// 脱局场景（未注入）＝null（无服务面——解析自然产出 null、不抛错）。
    /// </summary>
    internal MatchCardService? CardService { get; private set; }

    /// <summary>
    /// 装配期注入对局卡牌服务（S9；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：与装配一致（先于卡加载；显式、可测试——无隐藏全局单例）。
    /// </summary>
    /// <exception cref="ArgumentNullException">service 为 null。</exception>
    /// <exception cref="InvalidOperationException">卡牌服务已注入（重复注入被拒绝）。</exception>
    internal void ConfigureCardService(MatchCardService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (CardService is not null)
        {
            throw new InvalidOperationException("玩家卡牌服务已注入（重复注入被拒绝）。");
        }

        CardService = service;
    }

    /// <summary>
    /// 效果运行时门面（E1 加性面；internal）：对局装配期注入——「卡 → 玩家 → 服务」读取路径的玩家环节
    /// （csx handler 经 <c>Orc.Game.Effects.EffectRuntime.ResolveFor</c> 取用——消灭/伤害/修饰/词条/抽牌/无头选靶）；
    /// 脱局场景（未注入）＝null（无服务面——解析自然产出 null、不抛错）。
    /// </summary>
    internal Orc.Game.Effects.EffectRuntime? EffectRuntime { get; private set; }

    /// <summary>
    /// 装配期注入效果运行时门面（E1；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// </summary>
    /// <exception cref="ArgumentNullException">runtime 为 null。</exception>
    /// <exception cref="InvalidOperationException">效果运行时门面已注入（重复注入被拒绝）。</exception>
    internal void ConfigureEffectRuntime(Orc.Game.Effects.EffectRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (EffectRuntime is not null)
        {
            throw new InvalidOperationException("玩家效果运行时门面已注入（重复注入被拒绝）。");
        }

        EffectRuntime = runtime;
    }

    /// <summary>
    /// 资源管理器（E1-25 后续；internal）：对局装配期注入——「卡 → 玩家 → 服务」读取路径的玩家环节
    /// （卡级扣费/退点经 <c>ResourceManager.ResolveFor</c> 取用；通用无语义来源统一走 <c>ChangePointsAsync</c>）；
    /// 脱局场景（未注入）＝null（无服务面——相关解析自然产出 null、降级为直写）。
    /// </summary>
    internal ResourceManager? ResourceManager { get; private set; }

    /// <summary>
    /// 装配期注入资源管理器（E1-25 后续；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：随管理器群创建（先于任何卡加载与回合开始）。
    /// </summary>
    /// <exception cref="ArgumentNullException">manager 为 null。</exception>
    /// <exception cref="InvalidOperationException">资源管理器已注入（重复注入被拒绝）。</exception>
    internal void ConfigureResourceManager(ResourceManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (ResourceManager is not null)
        {
            throw new InvalidOperationException("玩家资源管理器已注入（重复注入被拒绝）。");
        }

        ResourceManager = manager;
    }

    // ---------- 玩家构筑配置（S10「G12补」加性面：主国/盟国——卡组构筑配置的读取承载） ----------

    /// <summary>
    /// 玩家构筑配置（S10 加性面；internal）：对局装配期注入（创建输入 → 创建期校验 → 初始化注入）——
    /// 「主国/盟国」卡组构筑配置的唯一数据来源（只读属性；值不可变——不存在双真源冲突）。
    /// 缺省语义：未提供配置＝不注入＝null（读面返回 null、不抛错；消费端降级为「不匹配」）。
    /// </summary>
    internal PlayerDeckConfiguration? DeckConfiguration { get; private set; }

    /// <summary>
    /// 主国（S10 读取面；只读转发构筑配置——未配置＝null、不抛错）。值域＝五主国（德/苏/美/英/日）；
    /// 「主国牌」筛选＝卡牌国籍==本值（读取消费在筛选侧组合——不新增机制级筛选 API）。
    /// </summary>
    public string? MainFaction => DeckConfiguration?.MainFaction;

    /// <summary>盟国（S10 读取面；只读转发构筑配置——未配置＝null、不抛错）。值域＝余下 4 个主国＋5 个盟国（9 值）。</summary>
    public string? AllyFaction => DeckConfiguration?.AllyFaction;

    /// <summary>
    /// 装配期注入玩家构筑配置（S10；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：玩家注入段（与环境/随机服务注入同序、先于卡组洗切与加载）。未提供配置＝不调用本方法（读面 null——缺省语义）。
    /// </summary>
    /// <exception cref="ArgumentNullException">configuration 为 null。</exception>
    /// <exception cref="InvalidOperationException">构筑配置已注入（重复注入被拒绝）。</exception>
    internal void ConfigureDeckConfiguration(PlayerDeckConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (DeckConfiguration is not null)
        {
            throw new InvalidOperationException("玩家构筑配置已注入（重复注入被拒绝）。");
        }

        DeckConfiguration = configuration;
    }

    // ---------- 对局历史读取服务（S10「G14补」加性面） ----------

    /// <summary>
    /// 对局历史读取服务（S10 加性面；internal）：对局装配期注入——「卡 → 玩家 → 服务」读取路径的玩家环节
    /// （效果运行期经 <see cref="MatchHistoryService.ResolveFor"/> 取用——「按条目类型筛取＋时序取用」的
    /// 最小历史读取面）；脱局场景（未注入）＝null（无服务面——解析自然产出 null、不抛错）。
    /// </summary>
    internal MatchHistoryService? HistoryService { get; private set; }

    /// <summary>
    /// 装配期注入对局历史读取服务（S10；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：与装配一致（先于卡加载；显式、可测试——无隐藏全局单例）。
    /// </summary>
    /// <exception cref="ArgumentNullException">service 为 null。</exception>
    /// <exception cref="InvalidOperationException">历史服务已注入（重复注入被拒绝）。</exception>
    internal void ConfigureHistoryService(MatchHistoryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (HistoryService is not null)
        {
            throw new InvalidOperationException("玩家历史服务已注入（重复注入被拒绝）。");
        }

        HistoryService = service;
    }

    // ---------- 目标选择管理器（C2 加性面） ----------

    /// <summary>
    /// 目标选择管理器（C2 加性面；internal）：对局装配期注入——「卡 → 玩家 → 服务」读取路径的玩家环节
    /// （效果运行期经 <see cref="TargeterManager.ResolveFor"/> 取用——交互发起面〔如开发/发现链的卡牌选择器出题〕）；
    /// 脱局场景（未注入）＝null（无服务面——解析自然产出 null、不抛错）。
    /// </summary>
    internal TargeterManager? TargeterManager { get; private set; }

    /// <summary>
    /// 装配期注入目标选择管理器（C2；由对局装配路径调用——一次性注入；重复注入＝明确拒绝（fail-fast）。
    /// 时序：与装配一致（先于卡加载；显式、可测试——无隐藏全局单例）。
    /// </summary>
    /// <exception cref="ArgumentNullException">manager 为 null。</exception>
    /// <exception cref="InvalidOperationException">目标选择管理器已注入（重复注入被拒绝）。</exception>
    internal void ConfigureTargeterManager(TargeterManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (TargeterManager is not null)
        {
            throw new InvalidOperationException("玩家目标选择管理器已注入（重复注入被拒绝）。");
        }

        TargeterManager = manager;
    }
}
