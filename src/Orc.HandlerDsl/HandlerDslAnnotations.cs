namespace Orc.Game.Effects.HandlerDsl;

// ─────────────────────────────────────────────────────────────────────────────
// handler DSL 的**标注语言**（SPEC §12.4）。命名空间固定 `Orc.Game.Effects.HandlerDsl`
// （写进 SPEC，避免作者侧与引擎侧各起一套）；标注**只标在 Orc.Game 的动作面类型／方法／参数上**。
//
// 归属切分（D8）：契约包只定义「标注这门语言」（有哪些标注、字段、语义）；
// 「某方法标了什么」只存在程序集里——本文件就是那门语言，不含任何 per-method 实例。
//
// 标注字段的取值类型按 R16 封闭集（不为标注另造一套 DSL）：
//   引脚类型名用 `entity`/`number`/`string`/`boolean`/`list<…>` 的规范文本；
//   实体子类型名用 `unit`/`hq`/`card`。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 登记一个**动作面**类型（SPEC §12.5）：其全部 public 方法进入目录扫描。
/// <para>
/// 扫描输入集是**显式登记**的，不走鸭子类型判据——「哪些该进目录」是产品判断、不是签名能推出的。
/// 新增门面只加本特性：扫描器不动、契约包不动、前端不动。
/// </para>
/// <para>
/// **新动作优先加在非冻结门面**（<c>KeywordEffectActionFace</c>）：落 <c>EffectRuntime</c> 属引擎自己的
/// 契约变更，须先过 <c>ContractFreezeTests</c>（重生成 <c>contract-freeze.json</c>）。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class HandlerDslActionFaceAttribute : Attribute
{
}

/// <summary>
/// 显式节点名（SPEC §12.2）：冲突或改名时**必须**给出，**显式名优先于派生名**。
/// <para>
/// 节点名是契约可见标识（<c>node-unknown</c> 按它判），方法名只是实现细节：
/// 方法一改名，所有已发布草案就会 <c>node-unknown</c>，只有显式标注能把两者解耦。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class HandlerDslNodeAttribute : Attribute
{
    /// <summary>构造。</summary>
    /// <param name="name">节点名（契约可见标识）。</param>
    public HandlerDslNodeAttribute(string name) => Name = name;

    /// <summary>节点名。</summary>
    public string Name { get; }
}

/// <summary>
/// 显式引脚名／类型覆盖／必填性覆盖（SPEC §12.4）。
/// <para>参数改名必须保留本标注里的旧引脚名，否则就是**删除引脚**（破坏性变更）。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class HandlerDslPinAttribute : Attribute
{
    /// <summary>构造。</summary>
    /// <param name="name">引脚名（契约可见）。</param>
    /// <param name="type">引脚类型覆盖（封闭集文本名；null＝按签名映射）。</param>
    /// <param name="required">必填性覆盖（null＝按默认值判定）。</param>
    public HandlerDslPinAttribute(string name, string? type = null, bool? required = null)
    {
        Name = name;
        Type = type;
        Required = required;
    }

    /// <summary>引脚名。</summary>
    public string Name { get; }

    /// <summary>引脚类型覆盖（null＝按签名映射）。</summary>
    public string? Type { get; }

    /// <summary>必填性覆盖（null＝按默认值判定）。</summary>
    public bool? Required { get; }
}

/// <summary>
/// 结果对象的多输出拆解（SPEC §12.4）。**结果对象未标注＝目录生成失败**（不静默退化成一个 <c>string</c> 引脚）。
/// <para>
/// 取值为常量字符串数组，每项形如 <c>"name:type"</c>（标量输出）或 <c>"name:type:enum"</c>
/// （枚举输出：<c>string</c> ＋ 允许取值集另由 <see cref="HandlerDslEnumValuesAttribute"/> 给出）。
/// 生成的 csx 按 <c>PascalCase(name)</c> 读结果的同名属性。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class HandlerDslResultAttribute : Attribute
{
    /// <summary>构造。</summary>
    /// <param name="outputs"><c>"name:type"</c> 或 <c>"name:type:enum"</c> 形式的输出清单（按目录顺序）。</param>
    public HandlerDslResultAttribute(params string[] outputs) => Outputs = outputs ?? Array.Empty<string>();

    /// <summary>输出拆解声明。</summary>
    public IReadOnlyList<string> Outputs { get; }
}

/// <summary>
/// <c>string</c> 引脚的允许取值集（枚举用 <c>string</c> ＋ 本标注表达，SPEC §6.3）。
/// <para>标注在参数上＝输入引脚的取值集；标注在返回值上＝单输出的取值集。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class HandlerDslEnumValuesAttribute : Attribute
{
    /// <summary>构造。</summary>
    /// <param name="values">允许取值集。</param>
    public HandlerDslEnumValuesAttribute(params string[] values) => Values = values ?? Array.Empty<string>();

    /// <summary>允许取值集（顺序即目录给出的顺序）。</summary>
    public IReadOnlyList<string> Values { get; }
}

/// <summary>
/// 实体引脚的运行期接受子类型集（<c>unit</c>／<c>hq</c>／<c>card</c>，SPEC §6.4）。
/// <para>仅供前端连线提示与静态子类型判定；图校验期只对**基类型**做硬拒绝。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false, Inherited = false)]
public sealed class HandlerDslSubtypeAttribute : Attribute
{
    /// <summary>构造。</summary>
    /// <param name="types">子类型集（封闭集：unit／hq／card）。</param>
    public HandlerDslSubtypeAttribute(params string[] types) => Types = types ?? Array.Empty<string>();

    /// <summary>运行期接受的子类型集。</summary>
    public IReadOnlyList<string> Types { get; }
}
