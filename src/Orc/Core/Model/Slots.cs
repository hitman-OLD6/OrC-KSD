using System.Text.Json;

namespace Orc.Core.Model;

/// <summary>
/// **空位类型集合（R39 裁决 3）＝六种，封闭**：<c>number</c>／<c>string</c>／<c>bool</c>／<c>enum</c>／
/// <c>asset-ref</c>／<c>handler</c>。
/// <para>
/// **不含结构类型**（能填「一个成员列表」的空位）：那会让 DSL 变成宏系统，展开器要从「合并」重写成「解释器」。
/// 因此 <c>args</c> 永远只是一层扁平具名表，不存在嵌套的参数形态。
/// </para>
/// <para>
/// **两层作用域共用这一套类型**（R39 裁决 8）：区别只是作用域——
/// 资产级 <c>asset.slots[]</c> 只接受前五种（绑定整个资产，多成员可引用同名），
/// 成员级 <c>members[].slot</c> **只接受 <c>handler</c>**（绑定单个成员）。
/// 放错层＝拒绝，不是「换个地方也行」。
/// </para>
/// </summary>
public enum SlotType
{
    /// <summary>数值（资产级）。</summary>
    Number,

    /// <summary>字符串（资产级）。</summary>
    String,

    /// <summary>布尔（资产级）。</summary>
    Bool,

    /// <summary>枚举（资产级；取值集声明在空位上，装载期校验）。</summary>
    Enum,

    /// <summary>资产引用（资产级；值＝应用层 ID，版本解析沿用 ID Bridge／lock，不另造版本语义）。</summary>
    AssetRef,

    /// <summary>处理器（成员级；值形态沿用既有三选一 `ComponentHandlerAsset`）。</summary>
    Handler,
}

/// <summary>空位类型的文本形态（与资产 JSON 的 <c>type</c> 字段一一对应；唯一真源）。</summary>
public static class SlotTypes
{
    /// <summary>全部类型名（顺序＝对外展示顺序）。</summary>
    public static IReadOnlyList<string> All { get; } =
        new[] { "number", "string", "bool", "enum", "asset-ref", "handler" };

    /// <summary>资产级允许的类型名（前五种；<c>handler</c> 不在此列，裁决 8）。</summary>
    public static IReadOnlyList<string> AssetLevel { get; } =
        new[] { "number", "string", "bool", "enum", "asset-ref" };

    /// <summary>类型 → 文本。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未知类型。</exception>
    public static string Format(SlotType type) => type switch
    {
        SlotType.Number => "number",
        SlotType.String => "string",
        SlotType.Bool => "bool",
        SlotType.Enum => "enum",
        SlotType.AssetRef => "asset-ref",
        SlotType.Handler => "handler",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知空位类型。"),
    };

    /// <summary>文本 → 类型（大小写与首尾空白不敏感；未知＝false，封闭集不做模糊匹配）。</summary>
    public static bool TryParse(string? text, out SlotType type)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "number":
                type = SlotType.Number;
                return true;
            case "string":
                type = SlotType.String;
                return true;
            case "bool":
                type = SlotType.Bool;
                return true;
            case "enum":
                type = SlotType.Enum;
                return true;
            case "asset-ref":
                type = SlotType.AssetRef;
                return true;
            case "handler":
                type = SlotType.Handler;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>该类型是否可出现在资产级（前五种）。</summary>
    public static bool IsAssetLevel(SlotType type) => type != SlotType.Handler;
}

/// <summary>
/// **资产级空位声明**（R39 裁决 8）：<c>asset.slots[]</c>——绑定**整个资产**，多个成员可引用同一个名字。
/// <para>
/// **状态只用「有没有 <c>value</c>」表达（D46）**，不另设标志位：
/// <list type="bullet">
///   <item><c>slot ∧ ¬value</c> ⇒ 未填（⇒ 抽象，S28）；</item>
///   <item><c>slot ∧ value</c> ⇒ 已填但**可再填**（<c>Instantiate</c> 的 <c>args</c> 覆盖它＝再填一次）；</item>
///   <item><c>slot ∧ value ∧ sealed</c> ⇒ 不可再填（<c>sealed</c> 是唯一禁止再填的机制）。</item>
/// </list>
/// </para>
/// <para>
/// **值类型封闭**：值必须与 <see cref="Type"/> 相符；<c>enum</c> 的值必须在 <see cref="EnumValues"/> 里
/// （装载期校验，不推迟到运行期）。
/// </para>
/// </summary>
public sealed class AssetSlotDeclaration
{
    /// <summary>创建一条资产级空位声明。</summary>
    /// <param name="name">空位名（<c>args</c> 的键；跨层唯一，不得与成员名重名）。</param>
    /// <param name="type">类型（只接受前五种；<c>handler</c> 属成员级）。</param>
    /// <param name="value">声明期已填的值（null＝未填）。</param>
    /// <param name="isSealed">禁止再填（<c>sealed</c>；唯一禁止再填的机制）。</param>
    /// <param name="enumValues"><c>enum</c> 的取值集（只有 <c>enum</c> 可声明；其余类型＝拒绝）。</param>
    /// <exception cref="ArgumentException">名字非法／放错层／取值集非法／值与类型不符／值不在取值集内。</exception>
    public AssetSlotDeclaration(
        string name,
        SlotType type,
        object? value = null,
        bool isSealed = false,
        IReadOnlyList<string>? enumValues = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("空位名不能为空或纯空白。", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(new[] { '#', ':', '/', ' ', '\t' }) >= 0)
        {
            throw new ArgumentException(
                $"空位名 '{name}' 含非法字符（不得含空白、'#'、':'、'/'：它是 args 的扁平键）。", nameof(name));
        }

        if (!SlotTypes.IsAssetLevel(type))
        {
            throw new ArgumentException(
                $"空位 '{trimmed}' 的类型 handler 只能挂在成员级（members[].slot）："
                + "handler 天然对应成员的执行体，搬到资产级需要额外的 slot→member 指向（R39 裁决 8）。",
                nameof(type));
        }

        // 四类标量在展开期注入为**同名私有字段**（裁决 14），handler 用 `owner.<slot名>` 读取——
        // 因此名字必须是合法标识符（与私有字段同一约束）。asset-ref 不注入，保持宽松。
        if (type != SlotType.AssetRef && !ViewFieldDefinition.IsIdentifier(trimmed))
        {
            throw new ArgumentException(
                $"空位 '{trimmed}' 的名字不是合法标识符：{SlotTypes.Format(type)} 空位会被注入为同名私有字段，"
                + "handler 只能通过 owner.<名字> 读取（R39 裁决 14）。",
                nameof(name));
        }

        if (type == SlotType.Enum)
        {
            if (enumValues is null || enumValues.Count == 0)
            {
                throw new ArgumentException($"空位 '{trimmed}' 是 enum 但没有声明取值集（values）。", nameof(enumValues));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in enumValues)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    throw new ArgumentException($"空位 '{trimmed}' 的取值集含空白项。", nameof(enumValues));
                }

                if (!seen.Add(item))
                {
                    throw new ArgumentException($"空位 '{trimmed}' 的取值集含重复值 '{item}'。", nameof(enumValues));
                }
            }
        }
        else if (enumValues is not null)
        {
            throw new ArgumentException(
                $"空位 '{trimmed}' 的类型是 {SlotTypes.Format(type)}，不能声明取值集（只有 enum 可以）。",
                nameof(enumValues));
        }

        Name = trimmed;
        Type = type;
        EnumValues = enumValues?.ToArray();
        IsSealed = isSealed;
        Value = NormalizeValue(value, nameof(value));
    }

    /// <summary>空位名（<c>args</c> 的键；跨层唯一）。</summary>
    public string Name { get; }

    /// <summary>类型（前五种之一）。</summary>
    public SlotType Type { get; }

    /// <summary><c>enum</c> 的取值集（其余类型＝null）。</summary>
    public IReadOnlyList<string>? EnumValues { get; }

    /// <summary>声明期已填的值（null＝未填）。</summary>
    public object? Value { get; }

    /// <summary>禁止再填（唯一禁止再填的机制）。</summary>
    public bool IsSealed { get; }

    /// <summary>是否已填（**唯一判据**是 <see cref="Value"/> 是否为 null）。</summary>
    public bool IsFilled => Value is not null;

    /// <summary>再填一次（<c>args</c> 覆盖已填值＝同一张表的另一半，D46）。</summary>
    /// <exception cref="ArgumentException">本空位已 <c>sealed</c>；或值与类型不符。</exception>
    public AssetSlotDeclaration WithValue(object? value)
    {
        if (IsSealed && value is not null)
        {
            throw new ArgumentException(
                $"空位 '{Name}' 已 sealed：sealed 是唯一禁止再填的机制（没有第二套『不可覆盖』开关）。",
                nameof(value));
        }

        return new AssetSlotDeclaration(Name, Type, value, IsSealed, EnumValues);
    }

    private object? NormalizeValue(object? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        switch (Type)
        {
            case SlotType.Number when TryDecimal(value, out var number):
                return number;
            case SlotType.String when value is string text:
                return text;
            case SlotType.Bool when value is bool flag:
                return flag;
            case SlotType.Enum when value is string option:
                if (EnumValues is null || !EnumValues.Contains(option, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"空位 '{Name}' 的值 '{option}' 不在取值集（{string.Join("／", EnumValues ?? Array.Empty<string>())}）。",
                        paramName);
                }

                return option;
            case SlotType.AssetRef when value is string reference && !string.IsNullOrWhiteSpace(reference):
                return reference.Trim();
            default:
                throw new ArgumentException(
                    $"空位 '{Name}' 的声明值类型 {value.GetType().Name} 不符合声明类型 {SlotTypes.Format(Type)}。",
                    paramName);
        }
    }

    private static bool TryDecimal(object value, out decimal number)
    {
        switch (value)
        {
            case decimal d:
                number = d;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case short s:
                number = s;
                return true;
            case byte b:
                number = b;
                return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                number = (decimal)f;
                return true;
            case double dbl when !double.IsNaN(dbl) && !double.IsInfinity(dbl):
                number = (decimal)dbl;
                return true;
            default:
                number = default;
                return false;
        }
    }
}

/// <summary>
/// **成员级空位声明**：<c>members[].slot</c>——**只接受 <c>handler</c>**，绑定单个成员。
/// <para>
/// 与 <see cref="ComponentMemberAsset.Handler"/> 是**正交的两个字段**（D40）：
/// 本字段承载「这里有一个空位、类型是 handler」，<c>handler</c> 承载「现在填的是什么」。
/// 四行判定表直接对应两个字段的存在性组合，不另设状态位。
/// </para>
/// </summary>
public sealed record MemberSlotDeclaration
{
    /// <summary>创建成员级空位声明（只接受 <c>handler</c>）。</summary>
    /// <exception cref="ArgumentException">类型不是 <c>handler</c>（五种标量必须挂资产级）。</exception>
    public MemberSlotDeclaration(SlotType type)
    {
        if (type != SlotType.Handler)
        {
            throw new ArgumentException(
                $"成员级空位只接受 handler，收到 {SlotTypes.Format(type)}："
                + "五种标量类型挂成员级会让「两个成员共享同一个值」只能传两次，框架无法保证一致（R39 裁决 8）。",
                nameof(type));
        }

        Type = type;
    }

    /// <summary>类型（恒 <see cref="SlotType.Handler"/>）。</summary>
    public SlotType Type { get; }

    /// <summary>handler 空位的唯一形态（省得每次 new）。</summary>
    public static MemberSlotDeclaration Handler { get; } = new(SlotType.Handler);
}

/// <summary>空位所属的作用域（schema 的扁平行里保留，供前端分组；<c>args</c> 本身不分层）。</summary>
public enum SlotScope
{
    /// <summary>资产级（<c>asset.slots[]</c>）。</summary>
    Asset,

    /// <summary>成员级（<c>members[].slot</c>）。</summary>
    Member,
}

/// <summary>
/// **扁平 schema 的一行**（<see cref="SlotSchema.SchemaOf"/> 的产物）：两层合并后的**同一张表**。
/// <para>
/// 为什么扁平：<c>args</c> 就是一层扁平具名表（D42），任何「资产级 args ＋ 成员级 args」的两段式校验都是错的。
/// <see cref="Scope"/> 只用于编辑器分组展示，**不参与**校验与哈希。
/// </para>
/// </summary>
/// <param name="Name">键名（资产级＝空位名；成员级＝成员名）。</param>
/// <param name="Type">类型。</param>
/// <param name="Scope">作用域（仅展示用）。</param>
/// <param name="IsSealed">是否禁止再填。</param>
/// <param name="HasValue">是否已填（声明期值或成员 handler）。</param>
/// <param name="EnumValues"><c>enum</c> 取值集。</param>
/// <param name="RuntimeEffect">
/// 本空位是否**有运行期效果**（裁决 15）：<c>number</c>／<c>string</c>／<c>bool</c>／<c>enum</c> 注入私有字段、
/// <c>handler</c> 是成员实现 ⇒ true；<c>asset-ref</c> 仅为**声明期校验**（装载期存在性 ＋ 编辑器可选列表）
/// ⇒ false，前端据此提示「填了不会改变行为」。
/// </param>
public sealed record SlotSchemaEntry(
    string Name,
    SlotType Type,
    SlotScope Scope,
    bool IsSealed,
    bool HasValue,
    IReadOnlyList<string>? EnumValues = null,
    bool RuntimeEffect = true);

/// <summary>
/// **空位 schema**（R39「已确认」3）：由两层声明**合并**成一张扁平表，供编辑器／前端表单消费；
/// <see cref="Validate"/> 与哈希**都只吃这张表**（一个入口，不存在两套参数形态）。
/// </summary>
public static class SlotSchema
{
    /// <summary>
    /// 两层合并成扁平 schema（资产级在前、成员级按成员声明序在后；名字跨层唯一由
    /// <see cref="ComponentAsset"/> 的构造期校验保证）。
    /// </summary>
    /// <exception cref="ArgumentNullException">asset 为 null。</exception>
    public static IReadOnlyList<SlotSchemaEntry> SchemaOf(ComponentAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var entries = new List<SlotSchemaEntry>(asset.Slots.Count + asset.Members.Count);
        foreach (var slot in asset.Slots)
        {
            entries.Add(new SlotSchemaEntry(
                slot.Name, slot.Type, SlotScope.Asset, slot.IsSealed, slot.IsFilled, slot.EnumValues,
                RuntimeEffect: slot.Type != SlotType.AssetRef));
        }

        foreach (var member in asset.Members)
        {
            if (member.Slot is null)
            {
                continue;
            }

            entries.Add(new SlotSchemaEntry(
                member.Name, SlotType.Handler, SlotScope.Member, member.Sealed, member.Handler is not null));
        }

        return entries;
    }

    /// <summary>
    /// **合并后的扁平 schema**（S33）：从 <see cref="PrefabType"/> 读——资产级空位已沿 <c>Extends</c> 链合并
    /// （<see cref="AssetSlotMerge"/>），因此父链声明的空位对表单可见。
    /// <para>与 <see cref="SchemaOf(ComponentAsset)"/> 同一结构、同一顺序：资产级在前、成员级按展开表序在后。</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">type 为 null。</exception>
    public static IReadOnlyList<SlotSchemaEntry> SchemaOf(PrefabType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var entries = new List<SlotSchemaEntry>(type.Slots.Count + type.Members.Count);
        foreach (var slot in type.Slots)
        {
            entries.Add(new SlotSchemaEntry(
                slot.Name, slot.Type, SlotScope.Asset, slot.IsSealed, slot.IsFilled, slot.EnumValues,
                RuntimeEffect: slot.Type != SlotType.AssetRef));
        }

        foreach (var member in type.Members)
        {
            if (member.Slot is null)
            {
                continue;
            }

            entries.Add(new SlotSchemaEntry(
                member.Name, SlotType.Handler, SlotScope.Member, member.Sealed, member.HasHandler));
        }

        return entries;
    }

    /// <summary>schema 里可填的键集合（值表面的封闭集：**不能新增成员**，只能填这些键）。</summary>
    public static IReadOnlySet<string> KeysOf(ComponentAsset asset)
        => SchemaOf(asset).Select(static e => e.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 校验一组 <c>args</c>（**值表面**）：未知键／类型不符／enum 越界／已 sealed 仍被填／必需项缺失
    /// 一律拒绝（不宽容）。**结构型参数没有入口**——值只能是六种类型之一，装不进「新增成员」。
    /// </summary>
    /// <returns>错误清单（空＝通过）。</returns>
    /// <exception cref="ArgumentNullException">asset／args 为 null。</exception>
    public static IReadOnlyList<PrefabLoadError> Validate(
        ComponentAsset asset, IReadOnlyDictionary<string, object?> args)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(args);

        var errors = new List<PrefabLoadError>();
        var schema = SchemaOf(asset);
        var known = new HashSet<string>(schema.Select(static e => e.Name), StringComparer.Ordinal);

        foreach (var key in args.Keys)
        {
            if (!known.Contains(key))
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"args.{key}",
                    $"args 的键 '{key}' 不在资产 '{asset.Id}' 的空位 schema 内："
                    + "值表面只能填空位，不能新增成员（新增成员属结构扩展，只能由声明面表达，R39 裁决 3／10）。"));
            }
        }

        foreach (var entry in schema)
        {
            var present = args.TryGetValue(entry.Name, out var value);

            if (entry.IsSealed && present)
            {
                errors.Add(new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"args.{entry.Name}",
                    $"空位 '{entry.Name}' 已 sealed：不许再填（sealed 是唯一禁止再填的机制）。"));
                continue;
            }

            if (!present)
            {
                // **缺项不在这里判**（S32／D44）：饱和判定只由 `Expand` 做一次——这样摊平与实例化
                // 对同一份声明给出**逐条相同**的未填空位清单（成员名 ＋ 模板名）。
                continue;
            }

            if (ValidateValue(asset.Id, entry, value) is { } error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    /// <summary>
    /// 按 schema 的一行校验一个值（值表面与声明面共用同一套类型判定）。
    /// </summary>
    /// <returns>错误（null＝通过）。</returns>
    public static PrefabLoadError? ValidateValue(string assetId, SlotSchemaEntry entry, object? value)
    {
        ArgumentNullException.ThrowIfNull(entry);

        switch (entry.Type)
        {
            case SlotType.Number when value is null || !IsNumber(value):
                return TypeError(assetId, entry, "number");
            case SlotType.String when value is not string:
                return TypeError(assetId, entry, "string");
            case SlotType.Bool when value is not bool:
                return TypeError(assetId, entry, "bool");
            case SlotType.Enum when value is not string text:
                return TypeError(assetId, entry, "enum（字符串）");
            case SlotType.Enum when entry.EnumValues is { } allowed && !allowed.Contains((string)value!, StringComparer.Ordinal):
                return new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"args.{entry.Name}",
                    $"空位 '{entry.Name}' 的值 '{value}' 不在取值集（{string.Join("／", allowed)}）。");
            case SlotType.AssetRef when value is not string reference || string.IsNullOrWhiteSpace(reference):
                return TypeError(assetId, entry, "asset-ref（应用层 ID 字符串）");
            case SlotType.Handler when value is not HandlerSlotArgument:
                return new PrefabLoadError(
                    PrefabErrorCategories.Structure,
                    $"args.{entry.Name}",
                    $"空位 '{entry.Name}' 是 handler：值只接受三种 kind（csx／dsl／assembly），"
                    + $"收到 {value?.GetType().Name ?? "null"}（第四种形态＝拒绝，R39 裁决 11）。");
            default:
                return null;
        }
    }

    private static bool IsNumber(object value) => value
        is decimal or int or long or short or byte or float or double;

    private static PrefabLoadError TypeError(string assetId, SlotSchemaEntry entry, string expected)
        => new(
            PrefabErrorCategories.Structure,
            $"args.{entry.Name}",
            $"资产 '{assetId}' 的空位 '{entry.Name}' 声明为 {SlotTypes.Format(entry.Type)}："
            + $"值必须是 {expected}。");

    /// <summary>
    /// 把一段 JSON 对象读成 <c>args</c>（扁平表）：数字／字符串／布尔／<c>{kind:…}</c>。
    /// <para>
    /// **只认六种标量／引用值**：数组、嵌套对象（除 handler 的三种 kind）、未知 kind 一律拒绝——
    /// 这是「不给 args 开结构通道」的落地形态。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    /// <exception cref="PrefabLoadException">JSON 非法或值形态不受支持。</exception>
    public static IReadOnlyDictionary<string, object?> ReadArguments(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PrefabLoadException(PrefabErrorCategories.Structure, $"args 不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PrefabLoadException(PrefabErrorCategories.Structure, "args 必须是对象（一层扁平具名表）。");
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = ReadArgumentValue(property.Name, property.Value);
            }

            return values;
        }
    }

    private static object? ReadArgumentValue(string key, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetDecimal(out var number))
                {
                    throw new PrefabLoadException(
                        PrefabErrorCategories.Structure, $"args.{key} 的数字超出 decimal 可表达范围。");
                }

                return number;
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Object:
                return ReadHandlerArgument(key, value);
            default:
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"args.{key} 是 {value.ValueKind}：值表面只接受标量／引用值，不接受结构（没有结构型空位，R39 裁决 3）。");
        }
    }

    private static HandlerSlotArgument ReadHandlerArgument(string key, JsonElement value)
    {
        if (!value.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure, $"args.{key} 的 handler 值缺少 kind（只允许 csx／dsl／assembly）。");
        }

        var kind = kindElement.GetString()?.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "csx" when value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String:
                return HandlerSlotArgument.Csx(text.GetString()!);
            case "dsl" when value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String:
                return HandlerSlotArgument.Dsl(path.GetString()!);
            case "assembly" when value.TryGetProperty("key", out var asmKey) && asmKey.ValueKind == JsonValueKind.String:
                return HandlerSlotArgument.Assembly(asmKey.GetString()!);
            case "csx" or "dsl" or "assembly":
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure, $"args.{key} 的 handler（kind={kind}）缺少对应的值字段。");
            default:
                throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"args.{key} 的 handler kind='{kind}' 不受支持：只允许 csx／dsl／assembly 三选一"
                    + "（第四种形态＝拒绝；三种表示已经存在，不为空位再造一套，R39 裁决 11）。");
        }
    }
}

/// <summary>
/// **资产级空位的沿链合并（唯一实现）**：父的已合并表 ＋ 子声明 → 新的已合并表。
/// <para>
/// 规则（D48／S27）：
/// <list type="bullet">
///   <item>按名合并：父在前（保持声明序），子新增的按子声明序追加；</item>
///   <item>**类型沿链不可变**：同名条目的 <see cref="SlotType"/>（及 enum 取值集）必须逐字相同，
///     只允许「填值」或「封死」——改类型＝拒绝；</item>
///   <item>子给值＝再填一次（父已 <c>sealed</c> ⇒ 拒绝）；子不给值＝沿用父值；</item>
///   <item><c>sealed</c> 沿链向下累计（父封死 ⇒ 子不能再填）。</item>
/// </list>
/// </para>
/// </summary>
public static class AssetSlotMerge
{
    /// <summary>合并两层资产级空位。</summary>
    /// <param name="parent">父的已合并表（根＝空）。</param>
    /// <param name="child">子声明表。</param>
    /// <param name="error">失败原因（成功＝null）。</param>
    /// <returns>合并结果（失败＝父表原样）。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public static IReadOnlyList<AssetSlotDeclaration> Merge(
        IReadOnlyList<AssetSlotDeclaration> parent,
        IReadOnlyList<AssetSlotDeclaration> child,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        var order = new List<string>(parent.Count + child.Count);
        var byName = new Dictionary<string, AssetSlotDeclaration>(StringComparer.Ordinal);
        foreach (var slot in parent)
        {
            order.Add(slot.Name);
            byName[slot.Name] = slot;
        }

        foreach (var slot in child)
        {
            if (!byName.TryGetValue(slot.Name, out var existing))
            {
                order.Add(slot.Name);
                byName[slot.Name] = slot;
                continue;
            }

            if (existing.Type != slot.Type)
            {
                error = $"资产级空位 '{slot.Name}' 沿链改变了类型（{SlotTypes.Format(existing.Type)} → "
                    + $"{SlotTypes.Format(slot.Type)}）：类型不可宽化、也不可窄化，只能填值或 sealed（D48）。";
                return parent;
            }

            if (!SameEnumValues(existing.EnumValues, slot.EnumValues))
            {
                error = $"资产级空位 '{slot.Name}' 沿链改变了 enum 取值集：取值集属于类型，不能只改取值集（D48）。";
                return parent;
            }

            if (existing.IsSealed && slot.Value is not null)
            {
                error = $"资产级空位 '{slot.Name}' 已 sealed：不许再填（sealed 是唯一禁止再填的机制）。";
                return parent;
            }

            try
            {
                byName[slot.Name] = new AssetSlotDeclaration(
                    slot.Name,
                    existing.Type,
                    slot.Value ?? existing.Value,
                    existing.IsSealed || slot.IsSealed,
                    existing.EnumValues);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return parent;
            }
        }

        error = null;
        return order.Select(name => byName[name]).ToArray();
    }

    private static bool SameEnumValues(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.SequenceEqual(right, StringComparer.Ordinal);
    }
}

/// <summary>
/// **handler 空位的值形态**（D45／裁决 11）：沿用既有三选一，不为空位造第四种形态。
/// <para>
/// <c>csx</c> 存**文本**、<c>dsl</c> 存**路径引用**（不是内容）、<c>assembly</c> 存**键**——
/// 与 <see cref="ComponentHandlerAsset"/> 的 <c>Script</c>／<c>Dsl</c>／<c>AssemblyKey</c> 一一对应。
/// </para>
/// </summary>
public sealed record HandlerSlotArgument
{
    private HandlerSlotArgument(HandlerSlotKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>来源形态。</summary>
    public HandlerSlotKind Kind { get; }

    /// <summary>csx 文本／dsl 相对包根路径／程序集键（按 <see cref="Kind"/> 解释）。</summary>
    public string Text { get; }

    /// <summary>csx 文本。</summary>
    /// <exception cref="ArgumentException">文本为空白。</exception>
    public static HandlerSlotArgument Csx(string text)
        => new(HandlerSlotKind.Csx, Require(text, nameof(text)));

    /// <summary>handler DSL 文档的**路径引用**（不内联内容）。</summary>
    /// <exception cref="ArgumentException">路径为空白。</exception>
    public static HandlerSlotArgument Dsl(string path)
        => new(HandlerSlotKind.Dsl, Require(path, nameof(path)));

    /// <summary>程序集处理器键。</summary>
    /// <exception cref="ArgumentException">键为空白。</exception>
    public static HandlerSlotArgument Assembly(string key)
        => new(HandlerSlotKind.Assembly, Require(key, nameof(key)));

    /// <summary>调试文本。</summary>
    public override string ToString() => Kind switch
    {
        HandlerSlotKind.Csx => "csx",
        HandlerSlotKind.Dsl => "dsl:" + Text,
        _ => "assembly:" + Text,
    };

    private static string Require(string text, string paramName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("handler 空位的值不能为空或纯空白。", paramName);
        }

        return text;
    }
}

/// <summary>handler 空位值的来源（三选一；与 <see cref="HandlerSourceKind"/> 同构，但语义是「值」不是「资产」）。</summary>
public enum HandlerSlotKind
{
    /// <summary>csx 文本。</summary>
    Csx,

    /// <summary>handler DSL 文档路径。</summary>
    Dsl,

    /// <summary>程序集键。</summary>
    Assembly,
}
