using Orc.Core;
using Orc.Game.Targeting;
using Orc.Game.Triggers;

namespace Orc.Game.Cards;

/// <summary>
/// 指令卡（三大类之一）：打出即生效的主动效果载体（P4 指令归一）。
/// 数据组件装配（E 区差异化）：＝阵营〔国籍〕＋部署费合并组件（基类）；「指令无其他组件」——不装配对战/单位/指挥组件。
/// <para>
/// 触发器（P4）：
/// ①预打出触发器（费用校验）：默认无交互、零更新；装配方可经 <see cref="AddPrePlayHandler"/> 加 handler 捕获引用
///   （以 targeter 产出为主要场景、不强制唯一；捕获经 <see cref="CardTriggerView.CaptureBox"/> 提交；
///   可经捕获箱 CancelPrePlay 请求取消预打出——预打出段失败/取消 ⇒ 打出不发生）；
/// ②打出触发器：**退化为纯验证触发器**（保留 <see cref="CostCheckTrigger"/> 验证绑定——打出段复验指挥点），
///   不再承载「打出宣告 / 主动 handler 集 / 打出收尾」链事件。
/// </para>
/// <para>
/// 打出链改由 <c>OrderFlow</c> 承载（S13）：<c>Resolve / priority 0</c>＝<c>card.played</c>、
/// <c>Resolve / priority 100</c>＝按登记序执行全部 <see cref="Orc.Cards.ICastAction"/>（主动效果）、
/// <c>Finalize / priority 100</c>＝扣费/离手（游戏层注册）。主动 handler 集入口（原 <c>AddActiveHandler</c>）已删除——
/// 主动效果一律经 <c>AddEffect</c> 挂载的 <see cref="Orc.Cards.ActiveEffect{TView}"/> 表达。
/// </para>
/// 加载模板与其余装配沿用基类（<see cref="CardBase"/>）；持久化重建经基类扩展点。
/// </summary>
public class OrderCard : CardBase
{
    /// <summary>创建指令卡（触发器在构造期装配；
    /// 费用校验触发器按名绑定费用检查判定器——解析器缺省＝内置默认〔独立构造即可用〕）。</summary>
    /// <param name="engine">引擎（发射/触发）。</param>
    /// <param name="definition">卡牌定义。</param>
    /// <param name="validationJudicatorResolver">验证判定器解析器（按名解析——对局路径＝注册表解析；
    /// 缺省＝null＝独立构造路径——内置默认解析）。</param>
    /// <exception cref="ArgumentNullException">engine 或 definition 为 null。</exception>
    public OrderCard(
        LogicEngine engine,
        CardDefinition definition,
        Func<string, JudicatorBinding>? validationJudicatorResolver = null)
        : base(engine, definition)
    {
        PrePlayTrigger = new CostCheckTrigger("预打出触发器", this, validationJudicatorResolver);
        PlayTrigger = new CostCheckTrigger("打出触发器", this, validationJudicatorResolver);
    }

    /// <summary>预打出触发器（费用校验：指挥点验证；装配方 handler 捕获集经此挂载）。</summary>
    public Trigger<CardTriggerView> PrePlayTrigger { get; }

    /// <summary>打出触发器（纯验证：费用校验——打出段复验；链事件已迁至 <c>OrderFlow</c>）。</summary>
    public Trigger<CardTriggerView> PlayTrigger { get; }

    /// <summary>
    /// 手牌起始指向槽位声明（S2：由指令卡自行声明——基类不持 targeter）：指令＝手牌打出时指向单位/HQ
    /// （候选域由打出/交互路径填充）；槽位参数＝本卡（起始卡牌）。声明固定、随实例复用。
    /// </summary>
    public IReadOnlyList<Selector> HandOriginSlots { get; } =
        new Selector[] { SelectorTemplates.HandOrigin };

    /// <summary>
    /// 预打出 handler 装配入口（增强入口；装配/加载阶段注册——本批不设运行期动态注册）：
    /// 注册为预打出触发器事件（登记序触发；异常沿用引擎隔离）；handler 经视图 <c>CaptureBox</c> 提交捕获值
    /// （或经 targeter 交互后提交）。返回注册句柄（可用于撤销）。
    /// </summary>
    /// <exception cref="ArgumentNullException">handler 为 null。</exception>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="ArgumentOutOfRangeException">priority 越界。</exception>
    public TriggerRegistration AddPrePlayHandler(
        string name, Func<CardTriggerView, Context, CancellationToken, Task> handler, int priority = 0,
        object? registrant = null)
        => PrePlayTrigger.Register(name, handler, priority, registrant: registrant);
}
