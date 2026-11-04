using Orc.Core;

namespace Orc.Core.Model;

/// <summary>
/// **只读存档运行时**（本会话 S3／D53 的窄接口）：导出器经由它枚举实体与查类型——
/// <b>不暴露</b> <see cref="OrchestrationRuntime"/> 具体类型（其公开面含
/// <c>CreateEntity</c>／<c>ConstructComponent</c>／<c>GrantComponent</c>／<c>DisposeComponents</c> 等写面；
/// 「只读」若只是约定就等于没有——编译期不可达才是保证）。
/// </summary>
public interface ISaveRuntime
{
    /// <summary>本运行时的全部存活实体（引擎＋卡牌＋组件；与 <see cref="OrchestrationRuntime.Entities"/> 同源）。</summary>
    IReadOnlyList<OrchestrationEntity> Entities { get; }

    /// <summary>
    /// 某宿主上的组件实例（**装配序＝声明序**，数组顺序即身份与份数——D54：不按名字去重）。
    /// </summary>
    IReadOnlyList<ComponentAttachment> ComponentsOf(OrchestrationEntity host);

    /// <summary>按 id 解析预制体类型（目录解析；inline 键不在本入口的职责内——存档只解析**源**预制体）。</summary>
    /// <exception cref="KeyNotFoundException">未登记。</exception>
    PrefabType Resolve(string prefabId);
}

/// <summary>
/// **只读存档上下文**（本会话 S3／裁决 7／D53）：导出器的第二参数——非执行 <see cref="Context"/>。
/// <para>
/// 设计判据（全部为**否定**，与执行上下文切开）：
/// ①<b>不造帧</b>——存档不发射信号、不在事件流上新增节点（造帧的唯一示范会 <c>AttachTo</c> 事件流）；
/// ②<b>不暴露执行 <see cref="Context"/></b>——没有帧则 <c>ctx.Engine</c> 恒 null，
/// 作者写 <c>ctx.Engine?.X</code> 会静默走另一分支；需要执行期信息＝存档逻辑耦合了执行，应改为读实体自身状态；
/// ③<b>无写面</b>——存档是观察，不给 <c>Set</c>／<c>SetPrivateField</c> 任何通道；
/// ④<b>无 <c>Turn</c></b>、无 <c>Seat</c>／<c>TriggerCard</c>——存档不响应某个动作；
/// ⑤<b><see cref="Runtime"/> 永远非 null</b>——不存在「有引擎／没引擎」两条路径，也就不会静默走另一分支。
/// </para>
/// <para>
/// 本类型另承载**框架侧的存档读面**（S2／S4）：<see cref="ReadSlots"/>／<see cref="ReadPrivateFields"/>
/// 是 B（完整复制）与作者导出器共用的状态读取（存档读 <c>_state</c>、不读声明初值——D55 判据）；
/// <see cref="ExportAttachment"/> 是附件（一层动态挂载）的导出入口，**复用宿主同一服务解析路径**（S4）。
/// </para>
/// </summary>
public sealed class SaveContext
{
    private readonly JudicatorRegistry? _judicators;

    /// <summary>创建存档上下文（框架专用：注册表由存档入口传入、作者不可构造注入）。</summary>
    /// <exception cref="ArgumentNullException">runtime 为 null。</exception>
    internal SaveContext(ISaveRuntime runtime, JudicatorRegistry? judicators)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _judicators = judicators;
    }

    /// <summary>只读运行时窄接口（**恒非 null**）。</summary>
    public ISaveRuntime Runtime { get; }

    /// <summary>
    /// 读实体的**当前**私有字段值（S2／D55）：遍历 <see cref="PrefabType.PrivateFields"/> 声明序、
    /// 经 <see cref="OrchestrationEntity.GetPrivateField"/> 读 <c>_state</c>——
    /// <b>不读声明初值</b>（「存档读 <c>_state</c>、不读 <c>Type.Slots</c>」是 B 的实现判据）。
    /// <para>
    /// 实体引用（S2）：<c>entity</c> 型值是 <see cref="OrchestrationEntity"/> ⇒ 转 <see cref="Entity.Name"/>；
    /// <c>entity[]</c> 型值逐元素转名字。**不是实体 ⇒ 抛**（报字段名与实际类型名）——
    /// 不允许沉默地写一个悬空引用，也不做任意对象序列化（裁决 1 第三条／已知限制 C4）。
    /// </para>
    /// <para>
    /// **空位镜像**（S6 round-trip 实测补丁）：已填的标量空位在**展开产物**里会被注入成同名私有字段
    /// （S35；<see cref="PrefabExpander.Expand"/> 的 <c>InjectScalarFields</c>），而手写
    /// <see cref="PrefabDefinition"/>／目录实体没有这一步。若不补一条镜像，同一个实体存档后经
    /// <c>Restore</c>（走展开）再存档会**多出**这些键，裁决 5 的「未演化 ⇒ <c>M2</c> 与 <c>M1</c> 逐字相同」
    /// 就不成立。镜像只在「同名私有字段未声明」时补一条，值取空位当前值（此时它就是该字段的全部状态——
    /// 目录实体上这个字段根本不存在，没有人能改写它）；已声明同名私有字段时仍读 <c>_state</c>（D55：状态赢）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">实体型字段持有非实体对象。</exception>
    public IReadOnlyDictionary<string, object?> ReadPrivateFields(OrchestrationEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in entity.Type.PrivateFields)
        {
            var value = entity.GetPrivateField(field.Name);
            values[field.Name] = field.Type switch
            {
                ViewFieldType.Entity => EntityNameOf(entity, field.Name, value),
                ViewFieldType.EntityArray => EntityNamesOf(entity, field.Name, value),
                _ => value,
            };
        }

        foreach (var slot in entity.Type.Slots)
        {
            if (!slot.IsFilled
                || values.ContainsKey(slot.Name)
                || PrefabExpander.MapSlotType(slot.Type) is null)
            {
                continue;
            }

            values[slot.Name] = slot.Value;
        }

        return values;
    }

    /// <summary>
    /// 读实体类型的**已填**资产空位当前配置值（S2）：键＝空位名、值＝绑定值（类型封闭：
    /// string／number／boolean／enum(string)）。未填空位不出现——饱和判据（R39 裁决 4）保证
    /// 可实例化实体没有未填空位，出现即违约。
    /// </summary>
    public IReadOnlyDictionary<string, object?> ReadSlots(PrefabType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var slot in type.Slots)
        {
            if (!slot.IsFilled)
            {
                continue; // 未填空位：不可实例化实体的违约形态（饱和校验在装载期兜底），不写进存档
            }

            values[slot.Name] = slot.Value;
        }

        return values;
    }

    /// <summary>
    /// 导出一个**附件**（S4／裁决 2）：深度恒为 1——组件只挂宿主、不挂组件，故附件是一份
    /// <see cref="SaveAttachment"/>（嵌入的 <c>{ templateId, slots, privateFields }</c>，不嵌套 attachments）。
    /// <para>
    /// **复用宿主同一服务解析路径**（S4）：门户看附件的**源**（<c>extends</c> 公式同宿主、D52）、
    /// 服务解析与失败语义与根实体逐字同族——附件未声明 <c>save</c> ⇒ <b>宿主存档失败</b>（Q2「整体报错」的执行点），
    /// 错误列出该组件 id。导出器返回 null ⇒ 整个存档失败（Q5）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">附件不可存档（未声明 save／服务未解析／导出器返回 null）。</exception>
    public SaveAttachment ExportAttachment(OrchestrationEntity component)
    {
        ArgumentNullException.ThrowIfNull(component);

        var host = component.Host
            ?? throw new InvalidOperationException(
                $"存档失败：'{component.Name}' 不是附件（Host == null）——ExportAttachment 只服务"
                + "「宿主嵌入组件」场景；根实体走导出器自身的求值路径。");

        var moding = SaveEvaluation.ExportAttachment(component, this, _judicators);

        // 深度恒为 1：组件不能再挂组件（requires-host 的既有事实）；出现＝作者 moding 越界，拒绝而非静默截断。
        if (moding.Attachments.Count > 0)
        {
            throw new InvalidOperationException(
                $"存档失败：附件 '{moding.Extends}'（实体 '{component.Name}'）的 moding 携带了 "
                + $"{moding.Attachments.Count} 个 attachments——深度恒为 1（组件只能挂宿主，不挂组件），拒绝越界形态。");
        }

        return new SaveAttachment(moding.Extends, moding.Slots, moding.PrivateFields);
    }

    /// <summary>溯源公式（D51／D52 的唯一入口）：<c>type.Origin?.TemplateId ?? type.Id</c>——公开：B（Orc.Game）与测试共用。</summary>
    public static string ExtendsOf(OrchestrationEntity entity)
        => entity.Type.Origin?.TemplateId ?? entity.Type.Id;

    /// <summary>实体引用 → 名字；非实体 ⇒ 明确失败（报字段名与实际类型名，S2）。</summary>
    private static string? EntityNameOf(OrchestrationEntity entity, string fieldName, object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value is OrchestrationEntity referenced
            ? referenced.Name
            : throw new InvalidOperationException(
                $"存档失败：实体 '{entity.Name}' 的私有字段 '{fieldName}' 声明为 entity，"
                + $"但持有 {value.GetType().Name}（只支持 OrchestrationEntity ⇒ 存 name；"
                + "不做任意对象序列化，不允许悬空引用）。");
    }

    /// <summary>实体引用数组 → 名字数组；元素非实体 ⇒ 明确失败。</summary>
    private static object? EntityNamesOf(OrchestrationEntity entity, string fieldName, object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not Array array)
        {
            throw new InvalidOperationException(
                $"存档失败：实体 '{entity.Name}' 的私有字段 '{fieldName}' 声明为 entity[]，"
                + $"但持有 {value.GetType().Name}。");
        }

        var names = new string?[array.Length];
        for (var i = 0; i < array.Length; i++)
        {
            names[i] = EntityNameOf(entity, fieldName, array.GetValue(i));
        }

        return names;
    }
}
