using System.Text.Json;

namespace Orc.Core.Model;

/// <summary>
/// **出身记录**（R39 裁决 1／10／S30／S31）：产物携带的溯源元数据——「这个形态从哪个模板／草案 ＋ 哪组参数来」。
/// <para>
/// <b>不承担重建职责</b>（D45 修正裁决 1）：读档恢复靠快照里已含的展开形态；本记录只用于追溯与编辑器回填。
/// 因此 <see cref="Args"/> 允许**引用形态**（<c>dsl</c> 是路径、<c>assembly</c> 是键），不要求自足。
/// </para>
/// <para>
/// <b>永远不是依赖</b>（R8 照旧）：它既不是 <c>extends</c>、也不参与解析——模板／草案被删、
/// <see cref="TemplateId"/> 解析不到，产物照常装载。装载链**不得**因它缺失或失效而拒绝资产。
/// </para>
/// <para>
/// 两个落点、同一结构、同一个产出函数（D43／D44）：摊平产物带 <see cref="DraftId"/>；
/// <c>named</c> 实例带 <see cref="TemplateId"/>／<see cref="ArgsHash"/>／<see cref="Args"/>。
/// 字段可空——手写资产没有本记录。
/// </para>
/// </summary>
/// <param name="TemplateId">来源模板 id（<c>named</c> 实例；null＝非模板实例）。</param>
/// <param name="DraftId">来源草案 id（摊平产物；null＝非摊平产物）。</param>
/// <param name="ArgsHash"><c>args</c> 的规范化哈希（<c>sha256:…</c>；与 <c>inline</c> 缓存键同一函数算出）。</param>
/// <param name="Args">原始参数（人读／重放；引用形态允许，不要求自足）。</param>
public sealed record AssetOrigin(
    string? TemplateId = null,
    string? DraftId = null,
    string? ArgsHash = null,
    IReadOnlyDictionary<string, object?>? Args = null)
{
    /// <summary>摊平产物的出身记录（只有草案 id：摊平的参数已在产物 <c>slots[].value</c> 里）。</summary>
    /// <exception cref="ArgumentException">draftId 为空白。</exception>
    public static AssetOrigin ForDraft(string draftId)
    {
        if (string.IsNullOrWhiteSpace(draftId))
        {
            throw new ArgumentException("草案 id 不能为空或纯空白。", nameof(draftId));
        }

        return new AssetOrigin(DraftId: draftId.Trim());
    }

    /// <summary>
    /// 参数化实例的出身记录（模板 id ＋ 规范化哈希 ＋ 原始参数）。
    /// <para><paramref name="argsHash"/> 必须由 <see cref="SlotArguments.Hash"/> 产出——本类型不自己算。</para>
    /// </summary>
    /// <exception cref="ArgumentException">templateId／argsHash 为空白。</exception>
    public static AssetOrigin ForTemplate(
        string templateId, string argsHash, IReadOnlyDictionary<string, object?> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("模板 id 不能为空或纯空白。", nameof(templateId));
        }

        if (string.IsNullOrWhiteSpace(argsHash))
        {
            throw new ArgumentException("argsHash 不能为空或纯空白。", nameof(argsHash));
        }

        // 冻结一份副本：`origin.args` 是产物的一部分，之后调用方改自己的字典不应影响产物。
        return new AssetOrigin(
            templateId.Trim(),
            ArgsHash: argsHash.Trim(),
            Args: new Dictionary<string, object?>(args, StringComparer.Ordinal));
    }

    /// <summary>是否带任何来源信息（全空＝不值得写出）。</summary>
    public bool HasAny => TemplateId is not null || DraftId is not null || ArgsHash is not null;

    /// <summary>把 <see cref="Args"/> 写成 JSON 对象（键按 ordinal 排序，值形态封闭：标量／三种 handler）。</summary>
    internal void WriteArgs(Utf8JsonWriter writer)
    {
        if (Args is null)
        {
            return;
        }

        writer.WriteStartObject("args");
        foreach (var pair in Args.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            WriteArgValue(writer, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteArgValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case HandlerSlotArgument handler:
                writer.WriteStartObject();
                writer.WriteString("kind", handler.Kind switch
                {
                    HandlerSlotKind.Csx => "csx",
                    HandlerSlotKind.Dsl => "dsl",
                    _ => "assembly",
                });
                writer.WriteString(
                    handler.Kind switch
                    {
                        HandlerSlotKind.Csx => "text",
                        HandlerSlotKind.Dsl => "path",
                        _ => "key",
                    },
                    handler.Text);
                writer.WriteEndObject();
                break;
            default:
                // 值表面是封闭集（`SlotSchema.ReadArguments` 已拒结构）：走到这里＝调用方违约。
                throw new ArgumentException(
                    $"origin.args 的值类型 {value.GetType().Name} 不受支持（只允许标量／csx／dsl／assembly）。");
        }
    }

    /// <summary>读 <c>origin.args</c>（与写出同一封闭集）。</summary>
    internal static IReadOnlyDictionary<string, object?>? ReadArgs(JsonElement element, string source)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PrefabLoadException(PrefabErrorCategories.Structure, $"{source}：origin.args 必须是对象。");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetDecimal(out var number) => number,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => ReadHandlerArg(property.Name, property.Value, source),
                _ => throw new PrefabLoadException(
                    PrefabErrorCategories.Structure,
                    $"{source}：origin.args['{property.Name}'] 的形态不受支持（只允许标量／csx／dsl／assembly）。"),
            };
        }

        return values;
    }

    private static HandlerSlotArgument ReadHandlerArg(string key, JsonElement value, string source)
    {
        if (!value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
            throw new PrefabLoadException(
                PrefabErrorCategories.Structure, $"{source}：origin.args['{key}'] 的 handler 缺少 kind。");
        }

        return kind.GetString() switch
        {
            "csx" => HandlerSlotArgument.Csx(RequiredArgString(value, "text", key, source)),
            "dsl" => HandlerSlotArgument.Dsl(RequiredArgString(value, "path", key, source)),
            "assembly" => HandlerSlotArgument.Assembly(RequiredArgString(value, "key", key, source)),
            var other => throw new PrefabLoadException(
                PrefabErrorCategories.Structure,
                $"{source}：origin.args['{key}'] 的 handler kind='{other}' 不受支持（只允许 csx／dsl／assembly）。"),
        };
    }

    private static string RequiredArgString(JsonElement value, string name, string key, string source)
        => value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new PrefabLoadException(
                PrefabErrorCategories.Structure, $"{source}：origin.args['{key}'] 的 handler 缺少 '{name}'。");
}
