using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Orc.Core.Model;

/// <summary>
/// 组件资产 schema（S11：新资产 schema，<c>schemaVersion = 4</c>）。
/// <para>
/// 一份资产＝**一个组件实体预制体**：成员（视图声明 + 可选 handler）、挂载声明、包装声明、
/// 私有字段、装配时点、一次性标记、兜底信号。全部字段都是**模型里的名字**（<c>requires-host</c>／
/// <c>active-when</c>／<c>one-shot</c>／<c>mounted</c>…），旧 schema 的
/// <c>viewType</c>／<c>hooks</c>／<c>injects</c>／<c>band</c>／<c>seam</c> 一律**不可表达**。
/// </para>
/// <para>
/// 与旧 <c>EffectSnapshot</c> 的关系（R8）：新 schema **不做旧格式兼容读取**——读到旧字段名即
/// <see cref="PrefabErrorCategories.Structure"/> 拒绝并提示先跑迁移器；旧预制体仍由旧装载器读（批次 5 才退役）。
/// </para>
/// </summary>
public static class AssetSchema
{
    /// <summary>本 schema 版本（与旧 <c>EffectSnapshot.SchemaVersion = 3</c> 不重叠：读到 3 即旧格式）。</summary>
    public const int Version = 4;

    /// <summary>资产类别唯一取值（本 schema 只表达组件实体；卡牌/引擎预制体仍由类型预制体声明）。</summary>
    public const string ComponentKind = "component";

    /// <summary>资产文件后缀（装载器扫描用）。</summary>
    public const string FilePattern = "*.component.json";

    /// <summary>
    /// 旧 schema 的字段名（任一出现＝拒绝装载）：它们是"名字字符串挂载"时代的产物，
    /// 新 schema 里由成员地址／相位／视图字段清单取代（R1d／R1e／R1i）。
    /// </summary>
    public static IReadOnlyList<string> LegacyFields { get; } = new[]
    {
        "viewType", "hooks", "band", "injects", "wrappers", "seam", "seams",
        "mainTrigger", "otherTriggers", "disposingTrigger", "judicatorWrappers",
        "stableKey", "priority", "oneShot",
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>解析一份组件资产（失败＝<see cref="PrefabLoadException"/>，携带分类与定位）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    /// <exception cref="PrefabLoadException">结构非法、旧格式、字段超出封闭集等。</exception>
    public static ComponentAsset Read(string json, string source = "")
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw Error(PrefabErrorCategories.Structure, source, $"JSON 非法：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Error(PrefabErrorCategories.Structure, source, "资产根节点必须是对象。");
            }

            if (root.TryGetProperty("schemaVersion", out var versionElement)
                && versionElement.TryGetInt32(out var version)
                && version != Version)
            {
                throw Error(
                    PrefabErrorCategories.Structure,
                    source,
                    $"schemaVersion={version} 不是新 schema（{Version}）：新 schema 不做旧格式兼容读取（R8）。"
                    + "请先用迁移器把它转成组件资产（*.component.json）。");
            }

            RejectLegacyFields(root, source);

            if (!root.TryGetProperty("asset", out var assetElement) || assetElement.ValueKind != JsonValueKind.Object)
            {
                throw Error(PrefabErrorCategories.Structure, source, "缺少 asset 段（组件资产的唯一真源）。");
            }

            RejectLegacyFields(assetElement, source);
            return ReadAsset(assetElement, source);
        }
    }

    private static ComponentAsset ReadAsset(JsonElement element, string source)
    {
        var id = RequiredString(element, "id", source);
        var kind = OptionalString(element, "kind") ?? ComponentKind;
        if (!string.Equals(kind, ComponentKind, StringComparison.Ordinal))
        {
            throw Error(
                PrefabErrorCategories.Structure,
                source,
                $"kind='{kind}' 不受支持：新 schema 只表达组件资产（kind='{ComponentKind}'）；"
                + "卡牌/引擎由类型预制体声明（R3g／R6b）。");
        }

        var requiresHost = ParseHostKind(OptionalString(element, "requires-host") ?? "any", source);
        var activeWhen = ParseActiveWhen(OptionalString(element, "active-when"));
        var oneShot = OptionalBool(element, "one-shot") ?? false;
        var disposalSignal = OptionalString(element, "disposal-signal");

        var privateFields = new List<PrivateFieldDefinition>();
        if (element.TryGetProperty("private-fields", out var fieldsElement))
        {
            foreach (var field in Array(fieldsElement, "private-fields", source))
            {
                var name = RequiredString(field, "name", source);
                var type = ParseFieldType(RequiredString(field, "type", source), source, $"私有字段 '{name}'");
                object? initial = null;
                if (field.TryGetProperty("initial", out var initialElement))
                {
                    initial = ReadInitial(initialElement, type, name, source);
                }

                try
                {
                    privateFields.Add(new PrivateFieldDefinition(name, type, initial));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
                {
                    throw Error(PrefabErrorCategories.View, source, ex.Message);
                }
            }
        }

        var members = new List<ComponentMemberAsset>();
        if (element.TryGetProperty("members", out var membersElement))
        {
            foreach (var member in Array(membersElement, "members", source))
            {
                members.Add(ReadMember(member, source));
            }
        }

        var mounts = new List<MountDeclaration>();
        if (element.TryGetProperty("mounts", out var mountsElement))
        {
            foreach (var mount in Array(mountsElement, "mounts", source))
            {
                var target = RequiredString(mount, "target", source);
                var member = RequiredString(mount, "member", source);
                if (!target.Contains('#', StringComparison.Ordinal))
                {
                    throw Error(
                        PrefabErrorCategories.Addressing,
                        source,
                        $"mounts[].target='{target}' 缺少作用域前缀（形态＝作用域#成员名，R1d）。");
                }

                mounts.Add(new MountDeclaration(target, member));
            }
        }

        var wraps = new List<WrapDeclaration>();
        if (element.TryGetProperty("wraps", out var wrapsElement))
        {
            foreach (var wrap in Array(wrapsElement, "wraps", source))
            {
                var phase = ParsePhase(RequiredString(wrap, "phase", source), source);
                var handler = RequiredString(wrap, "handler", source);
                var judge = OptionalString(wrap, "judge");
                if (judge is not null)
                {
                    wraps.Add(new WrapDeclaration(WrapTarget.ForJudicator(judge), phase, handler));
                    continue;
                }

                var target = RequiredString(wrap, "target", source);
                if (!target.Contains('#', StringComparison.Ordinal))
                {
                    throw Error(
                        PrefabErrorCategories.Addressing,
                        source,
                        $"wraps[].target='{target}' 缺少作用域前缀（形态＝作用域#成员名，R1i）。");
                }

                wraps.Add(WrapDeclaration.ForMember(target, phase, handler));
            }
        }

        var judgeWraps = new List<ComponentJudgeWrapAsset>();
        if (element.TryGetProperty("judge-wraps", out var judgeElement))
        {
            foreach (var wrap in Array(judgeElement, "judge-wraps", source))
            {
                judgeWraps.Add(new ComponentJudgeWrapAsset(
                    RequiredString(wrap, "judge", source),
                    ParsePhase(RequiredString(wrap, "phase", source), source),
                    OptionalString(wrap, "entry") ?? "Handle",
                    RequiredString(wrap, "script", source)));
            }
        }

        return new ComponentAsset(
            id, requiresHost, activeWhen, oneShot, disposalSignal, privateFields, members, mounts, wraps, judgeWraps);
    }

    private static ComponentMemberAsset ReadMember(JsonElement member, string source)
    {
        var name = RequiredString(member, "name", source);
        var visibility = ParseVisibility(member, source);

        var view = ViewSpec.Empty;
        if (member.TryGetProperty("view", out var viewElement) && viewElement.ValueKind == JsonValueKind.Object
            && viewElement.TryGetProperty("fields", out var fieldList))
        {
            var fields = new List<ViewFieldDefinition>();
            foreach (var field in Array(fieldList, $"member '{name}' 的 view.fields", source))
            {
                var fieldName = RequiredString(field, "name", source);
                var type = ParseFieldType(RequiredString(field, "type", source), source, $"视图字段 '{fieldName}'");
                var writable = OptionalBool(field, "writable") ?? false;
                var required = OptionalBool(field, "required") ?? true;
                try
                {
                    fields.Add(new ViewFieldDefinition(fieldName, type, writable, required));
                }
                catch (ArgumentException ex)
                {
                    throw Error(PrefabErrorCategories.View, source, ex.Message);
                }
            }

            try
            {
                view = new ViewSpec(fields);
            }
            catch (ArgumentException ex)
            {
                throw Error(PrefabErrorCategories.View, source, ex.Message);
            }
        }

        ComponentHandlerAsset? handler = null;
        if (member.TryGetProperty("handler", out var handlerElement) && handlerElement.ValueKind == JsonValueKind.Object)
        {
            var id = RequiredString(handlerElement, "id", source);
            var entry = OptionalString(handlerElement, "entry") ?? "HandleAsync";
            var script = OptionalString(handlerElement, "script");
            var assemblyKey = OptionalString(handlerElement, "assembly-key");
            if (script is null && assemblyKey is null)
            {
                throw Error(
                    PrefabErrorCategories.Structure,
                    source,
                    $"成员 '{name}' 的 handler 既没有 script 也没有 assembly-key（无源码可编译）。");
            }

            handler = new ComponentHandlerAsset(id, entry, script, assemblyKey);
        }

        return new ComponentMemberAsset(name, visibility, view, handler);
    }

    private static object? ReadInitial(JsonElement element, ViewFieldType type, string field, string source)
        => type switch
        {
            ViewFieldType.Number => element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDecimal(),
                JsonValueKind.Null => null,
                _ => throw Error(PrefabErrorCategories.View, source, $"私有字段 '{field}' 的初值必须是数字。"),
            },
            ViewFieldType.String => element.ValueKind == JsonValueKind.String ? element.GetString() : null,
            ViewFieldType.Boolean => element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => throw Error(PrefabErrorCategories.View, source, $"私有字段 '{field}' 的初值必须是布尔。"),
            },
            _ => throw Error(
                PrefabErrorCategories.View,
                source,
                $"私有字段 '{field}' 的数组类型不支持内联初值（封闭集只允许标量初值；运行期经 owner 通道赋值）。"),
        };

    // ---------- 写出（迁移器与往返测试共用，单一真源） ----------

    /// <summary>把组件资产写成规范 JSON 文本（键序确定、2 空格缩进、LF、尾随换行）。</summary>
    /// <exception cref="ArgumentNullException">asset 为 null。</exception>
    public static string Write(ComponentAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", Version);
            writer.WriteStartObject("asset");
            writer.WriteString("kind", ComponentKind);
            writer.WriteString("id", asset.Id);
            writer.WriteString("requires-host", FormatHostKind(asset.RequiresHost));
            writer.WriteString("active-when", asset.ActiveWhen == ActiveWhen.Loaded ? "loaded" : "placed");
            writer.WriteBoolean("one-shot", asset.OneShot);
            if (asset.DisposalSignal is not null)
            {
                writer.WriteString("disposal-signal", asset.DisposalSignal);
            }

            writer.WriteStartArray("private-fields");
            foreach (var field in asset.PrivateFields)
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteString("type", ViewFieldTypes.Format(field.Type));
                WriteInitial(writer, field);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("members");
            foreach (var member in asset.Members)
            {
                writer.WriteStartObject();
                writer.WriteString("name", member.Name);
                writer.WriteStartArray("visibility");
                foreach (var flag in VisibilityNames(member.Visibility))
                {
                    writer.WriteStringValue(flag);
                }

                writer.WriteEndArray();
                writer.WriteStartObject("view");
                writer.WriteStartArray("fields");
                foreach (var field in member.View.Fields)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", field.Name);
                    writer.WriteString("type", ViewFieldTypes.Format(field.Type));
                    writer.WriteBoolean("writable", field.Writable);
                    writer.WriteBoolean("required", field.Required);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();

                if (member.Handler is { } handler)
                {
                    writer.WriteStartObject("handler");
                    writer.WriteString("id", handler.Id);
                    writer.WriteString("entry", handler.Entry);
                    if (handler.Script is not null)
                    {
                        writer.WriteString("script", handler.Script);
                    }

                    if (handler.AssemblyKey is not null)
                    {
                        writer.WriteString("assembly-key", handler.AssemblyKey);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("mounts");
            foreach (var mount in asset.Mounts)
            {
                writer.WriteStartObject();
                writer.WriteString("target", mount.Target);
                writer.WriteString("member", mount.Member);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("wraps");
            foreach (var wrap in asset.Wraps)
            {
                writer.WriteStartObject();
                if (wrap.Target.IsMember)
                {
                    writer.WriteString("target", wrap.Target.Address.ToString());
                }
                else
                {
                    writer.WriteString("judge", wrap.Target.Judicator);
                }

                writer.WriteString("phase", FormatPhase(wrap.Phase));
                writer.WriteString("handler", wrap.HandlerId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (asset.JudgeWraps.Count > 0)
            {
                writer.WriteStartArray("judge-wraps");
                foreach (var wrap in asset.JudgeWraps)
                {
                    writer.WriteStartObject();
                    writer.WriteString("judge", wrap.Judicator);
                    writer.WriteString("phase", FormatPhase(wrap.Phase));
                    writer.WriteString("entry", wrap.Entry);
                    writer.WriteString("script", wrap.Script);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
        return text + "\n";
    }

    private static void WriteInitial(Utf8JsonWriter writer, PrivateFieldDefinition field)
    {
        switch (field.Type)
        {
            case ViewFieldType.Number:
                if (field.InitialValue is IConvertible convertible)
                {
                    writer.WriteNumber("initial", convertible.ToDecimal(System.Globalization.CultureInfo.InvariantCulture));
                }

                break;
            case ViewFieldType.String:
                writer.WriteString("initial", field.InitialValue as string ?? string.Empty);
                break;
            case ViewFieldType.Boolean:
                writer.WriteBoolean("initial", field.InitialValue is true);
                break;
        }
    }

    /// <summary>可见性位标志 → 文本清单（<c>callable</c>／<c>hostable</c>／<c>mountable</c>）。</summary>
    public static IReadOnlyList<string> VisibilityNames(MemberVisibility visibility)
    {
        var names = new List<string>(3);
        if (visibility.HasFlag(MemberVisibility.Callable))
        {
            names.Add("callable");
        }

        if (visibility.HasFlag(MemberVisibility.Hostable))
        {
            names.Add("hostable");
        }

        if (visibility.HasFlag(MemberVisibility.Mountable))
        {
            names.Add("mountable");
        }

        return names;
    }

    /// <summary>文本清单 → 可见性位标志（未知词＝拒绝）。</summary>
    /// <exception cref="ArgumentException">出现未定义的能力词。</exception>
    public static MemberVisibility ParseVisibilityNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var visibility = MemberVisibility.None;
        foreach (var name in names)
        {
            visibility |= name.Trim().ToLowerInvariant() switch
            {
                "callable" => MemberVisibility.Callable,
                "hostable" => MemberVisibility.Hostable,
                "mountable" => MemberVisibility.Mountable,
                _ => throw new ArgumentException($"未定义的可见性能力 '{name}'（只允许 callable／hostable／mountable）。"),
            };
        }

        return visibility;
    }

    /// <summary>宿主需求 → 文本（<c>any</c>／<c>unit</c>／<c>unit|order</c>…）。</summary>
    public static string FormatHostKind(HostKind kind)
        => kind == HostKind.Any
            ? "any"
            : string.Join("|", new[] { HostKind.Unit, HostKind.Order, HostKind.Counter, HostKind.Hq }
                .Where(flag => (kind & flag) != 0)
                .Select(flag => flag.ToString().ToLowerInvariant()));

    /// <summary>包装相位 → 文本。</summary>
    public static string FormatPhase(WrapperPhase phase) => phase switch
    {
        WrapperPhase.Before => "before",
        WrapperPhase.After => "after",
        WrapperPhase.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "未定义的包装相位。"),
    };

    // ---------- 解析辅助 ----------

    private static MemberVisibility ParseVisibility(JsonElement member, string source)
    {
        if (!member.TryGetProperty("visibility", out var visibilityElement))
        {
            return MemberVisibility.None;
        }

        if (visibilityElement.ValueKind != JsonValueKind.Array)
        {
            throw Error(PrefabErrorCategories.Structure, source, "visibility 必须是字符串数组。");
        }

        var names = new List<string>();
        foreach (var item in visibilityElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Error(PrefabErrorCategories.Structure, source, "visibility 的每一项必须是字符串。");
            }

            names.Add(item.GetString()!);
        }

        try
        {
            return ParseVisibilityNames(names);
        }
        catch (ArgumentException ex)
        {
            throw Error(PrefabErrorCategories.Visibility, source, ex.Message);
        }
    }

    private static void RejectLegacyFields(JsonElement element, string source)
    {
        foreach (var property in element.EnumerateObject())
        {
            foreach (var legacy in LegacyFields)
            {
                if (string.Equals(property.Name, legacy, StringComparison.OrdinalIgnoreCase))
                {
                    throw Error(
                        PrefabErrorCategories.Structure,
                        source,
                        $"出现旧格式字段 '{property.Name}'：新 schema 不做旧格式兼容读取（R8），"
                        + "请先用迁移器把资产转成组件资产（*.component.json）。");
                }
            }
        }
    }

    private static HostKind ParseHostKind(string text, string source)
    {
        if (string.Equals(text.Trim(), "any", StringComparison.OrdinalIgnoreCase))
        {
            return HostKind.Any;
        }

        var kind = HostKind.None;
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            kind |= part.ToLowerInvariant() switch
            {
                "unit" => HostKind.Unit,
                "order" => HostKind.Order,
                "counter" => HostKind.Counter,
                "hq" => HostKind.Hq,
                _ => throw Error(
                    PrefabErrorCategories.Structure,
                    source,
                    $"requires-host 出现未定义的宿主类型 '{part}'（只允许 any／unit／order／counter／hq，可用 | 组合）。"),
            };
        }

        return kind == HostKind.None
            ? throw Error(PrefabErrorCategories.Structure, source, "requires-host 不能为空（组件必须声明宿主需求）。")
            : kind;
    }

    private static ActiveWhen ParseActiveWhen(string? text)
        => string.Equals(text?.Trim(), "loaded", StringComparison.OrdinalIgnoreCase)
            ? ActiveWhen.Loaded
            : ActiveWhen.Placed;

    private static WrapperPhase ParsePhase(string text, string source) => text.Trim().ToLowerInvariant() switch
    {
        "before" => WrapperPhase.Before,
        "after" => WrapperPhase.After,
        "replace" => WrapperPhase.Replace,
        _ => throw Error(
            PrefabErrorCategories.Structure,
            source,
            $"未定义的包装相位 '{text}'（只允许 before／after／replace，S2a）。"),
    };

    private static ViewFieldType ParseFieldType(string text, string source, string where)
    {
        if (ViewFieldTypes.TryParse(text, out var type))
        {
            return type;
        }

        throw Error(
            PrefabErrorCategories.View,
            source,
            $"{where} 的类型 '{text}' 超出封闭内建集：只允许 entity／number／string／boolean 及其数组形态（R1a）。");
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string where, string source)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error(PrefabErrorCategories.Structure, source, $"{where} 必须是数组。");
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Error(PrefabErrorCategories.Structure, source, $"{where} 的每一项必须是对象。");
            }

            yield return item;
        }
    }

    private static string RequiredString(JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error(PrefabErrorCategories.Structure, source, $"缺少必填字符串字段 '{name}'。");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? OptionalBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static PrefabLoadException Error(string category, string source, string message)
        => new(category, source.Length == 0 ? message : $"{source}：{message}");
}
