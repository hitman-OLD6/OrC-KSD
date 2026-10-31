using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Orc.Core.Model;

/// <summary>
/// 组件资产 schema（S11：新资产 schema，<c>schemaVersion = 4</c>）。
/// <para>
/// 一份资产＝**一个组件实体预制体**：成员（视图声明 + 可选 handler + 可选段声明 + 可选具名缝声明）、挂载声明、
/// 包装声明、私有字段、装配时点、一次性标记、兜底信号。全部字段都是**模型里的名字**（<c>requires-host</c>／
/// <c>active-when</c>／<c>one-shot</c>／<c>mounted</c>…），旧 schema 的
/// <c>viewType</c>／<c>hooks</c>／<c>injects</c> 等一律**不可表达**
/// （<c>band</c> 例外：R1i 修订 2026-10-29 起<b>合法</b>，但形态是新模型的成员级段名——字符串，
/// 见 <see cref="MemberDefinition.Band"/>；<c>seam</c>／<c>seams</c> 同理恢复为合法概念，
/// 形态是 <c>members[].seams</c>（<see cref="MemberDefinition.Seams"/>）＋ <c>wraps[].seam</c>
/// （<see cref="WrapDeclaration.Seam"/>）；旧 schema 的数值型 band／handler 级 seam 属旧格式，由迁移器转换）。
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

    /// <summary>
    /// **moding 草案**的类别取值（S11／D11）：与 <see cref="ComponentKind"/> 共用同一套 <c>asset</c> 形态，
    /// 差别只在语义——草案带 <c>extends</c>、只存增量，且以 <see cref="Orc.Core.AssetKind.Moding"/> 注册。
    /// </summary>
    public const string ModingKind = "moding";

    /// <summary>资产文件后缀（装载器扫描用）。</summary>
    public const string FilePattern = "*.component.json";

    /// <summary>
    /// 旧 schema 的字段名（任一出现＝拒绝装载）：它们是"名字字符串挂载"时代的产物，
    /// 新 schema 里由成员地址／相位／视图字段清单取代（R1d／R1e／R1i）。
    /// <para>
    /// <c>band</c> **不在此列**（R1i 修订 2026-10-29）：band 恢复为合法概念，形态是成员级段名字符串
    /// （<c>members[].band</c> ＋ 可选 <c>members[].bands</c>）。段序由成员的 bands 方案给出，
    /// 不再有数值型旧枚举。
    /// </para>
    /// <para>
    /// <c>seam</c>／<c>seams</c> **亦不在此列**（R1i 修订 2026-10-29：seam 恢复，不是退役）：
    /// 形态是成员级缝名（<c>members[].seams</c>）＋包装声明上的缝名（<c>wraps[].seam</c>）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LegacyFields { get; } = new[]
    {
        "viewType", "hooks", "injects", "wrappers",
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
        if (!string.Equals(kind, ComponentKind, StringComparison.Ordinal)
            && !string.Equals(kind, ModingKind, StringComparison.Ordinal))
        {
            throw Error(
                PrefabErrorCategories.Structure,
                source,
                $"kind='{kind}' 不受支持：新 schema 只表达组件资产（kind='{ComponentKind}'）与 moding 草案"
                + $"（kind='{ModingKind}'）；卡牌/引擎由类型预制体声明（R3g／R6b）。");
        }

        var extends = OptionalString(element, "extends");
        var authorizesModing = OptionalBool(element, "authorize-moding") ?? false;

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
                var seam = OptionalString(wrap, "seam");
                var judge = OptionalString(wrap, "judge");
                if (judge is not null)
                {
                    if (seam is not null)
                    {
                        throw Error(
                            PrefabErrorCategories.Structure,
                            source,
                            $"wraps[].seam='{seam}' 不能与 judge 同时出现：具名缝只能挂在成员目标上（判定器没有 handler 内部插入点）。");
                    }

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

                if (seam is null)
                {
                    wraps.Add(WrapDeclaration.ForMember(target, phase, handler));
                    continue;
                }

                // 缝内包装只有「前置」一种相位（缝在 handler 内部，"成员落地之后"在这里没有位置）。
                if (phase != WrapperPhase.Before)
                {
                    throw Error(
                        PrefabErrorCategories.Structure,
                        source,
                        $"wraps[].seam='{seam}' 只允许 phase=before（缝内包装恒为前置）；收到 phase='{FormatPhase(phase)}'。");
                }

                wraps.Add(WrapDeclaration.ForSeam(target, seam, handler));
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

        try
        {
            return new ComponentAsset(
                id, requiresHost, activeWhen, oneShot, disposalSignal, privateFields, members, mounts, wraps, judgeWraps,
                extends, authorizesModing);
        }
        catch (ArgumentException ex)
        {
            throw Error(PrefabErrorCategories.Structure, source, ex.Message);
        }
    }

    /// <summary>
    /// 读取**来源标记** <c>generated</c>（R28／S11）：`{ generator, catalogRevision, source, dslHash, textHash, nodeMap }`。
    /// <para>
    /// 三个哈希字段都是字符串（<c>sha256:…</c> 形态，由生成器/摊平器给出）；<c>nodeMap</c> 可省略——
    /// 它是调试可读性补偿，**不参与行为判定与 textHash**（D12）。
    /// </para>
    /// </summary>
    /// <exception cref="PrefabLoadException">结构不符（缺必填字段／nodeMap 形态非法）。</exception>
    private static HandlerGeneratedBlock ReadGenerated(JsonElement element, string memberName, string source)
    {
        var where = $"成员 '{memberName}' 的 handler.generated";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error(PrefabErrorCategories.Structure, source, $"{where} 必须是对象。");
        }

        if (!element.TryGetProperty("catalogRevision", out var revisionElement) || !revisionElement.TryGetInt32(out var revision))
        {
            throw Error(PrefabErrorCategories.Structure, source, $"{where} 缺少整数 catalogRevision。");
        }

        IReadOnlyDictionary<string, HandlerLineRange>? nodeMap = null;
        if (element.TryGetProperty("nodeMap", out var mapElement))
        {
            if (mapElement.ValueKind != JsonValueKind.Object)
            {
                throw Error(PrefabErrorCategories.Structure, source, $"{where}.nodeMap 必须是对象（节点路径 → [起, 止]）。");
            }

            var map = new Dictionary<string, HandlerLineRange>(StringComparer.Ordinal);
            foreach (var entry in mapElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() != 2)
                {
                    throw Error(
                        PrefabErrorCategories.Structure, source, $"{where}.nodeMap['{entry.Name}'] 必须是 [起, 止] 两元素数组。");
                }

                var start = entry.Value[0];
                var end = entry.Value[1];
                if (!start.TryGetInt32(out var startLine) || !end.TryGetInt32(out var endLine))
                {
                    throw Error(
                        PrefabErrorCategories.Structure, source, $"{where}.nodeMap['{entry.Name}'] 的行号必须是整数。");
                }

                map[entry.Name] = new HandlerLineRange(startLine, endLine);
            }

            nodeMap = map;
        }

        return new HandlerGeneratedBlock(
            RequiredString(element, "generator", source),
            revision,
            RequiredString(element, "source", source),
            RequiredString(element, "dslHash", source),
            RequiredString(element, "textHash", source),
            nodeMap);
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
            var dsl = OptionalString(handlerElement, "dsl");
            var dslHash = OptionalString(handlerElement, "dslHash");

            // 三选一互斥（D11）：script／assembly-key／dsl 恰好一种。互斥校验并入既有 structure 分类，不新增错误码。
            var sources = (script is not null ? 1 : 0) + (assemblyKey is not null ? 1 : 0) + (dsl is not null ? 1 : 0);
            if (sources == 0)
            {
                throw Error(
                    PrefabErrorCategories.Structure,
                    source,
                    $"成员 '{name}' 的 handler 既没有 script、也没有 assembly-key、也没有 dsl（无源码可编译）。");
            }

            if (sources > 1)
            {
                throw Error(
                    PrefabErrorCategories.Structure,
                    source,
                    $"成员 '{name}' 的 handler 同时声明了多种来源（script／assembly-key／dsl 三选一互斥）："
                    + $"{DescribeHandlerSources(script, assemblyKey, dsl)}。");
            }

            if (dslHash is not null && dsl is null)
            {
                throw Error(
                    PrefabErrorCategories.Structure,
                    source,
                    $"成员 '{name}' 的 handler 声明了 dslHash 却没有 dsl：dslHash 是 DSL 文档的内容快照哈希。");
            }

            handler = new ComponentHandlerAsset(id, entry, script, assemblyKey, dsl, dslHash);

            // 来源标记（R28／S11）：只有摊平产物会带 `generated`，草案恒无。
            if (handlerElement.TryGetProperty("generated", out var generatedElement))
            {
                handler = handler with { Generated = ReadGenerated(generatedElement, name, source) };
            }
        }

        var band = OptionalString(member, "band");
        var bands = ReadBands(member, source, $"成员 '{name}' 的 bands");
        if (band is not null && (bands is null || !bands.Contains(band, StringComparer.Ordinal)))
        {
            throw Error(
                PrefabErrorCategories.Structure,
                source,
                $"成员 '{name}' 的 band='{band}' 未在成员的 bands 方案中声明"
                + (bands is null ? "（未声明 bands：band 只能在段序方案内取值）。" : $"（已声明：{string.Join("／", bands)}）。"));
        }

        var seams = ReadSeams(member, source, $"成员 '{name}' 的 seams");
        var sealedMember = OptionalBool(member, "sealed") ?? false;

        return new ComponentMemberAsset(name, visibility, view, handler, band, bands, seams, sealedMember);
    }

    /// <summary>
    /// 读取可选的具名缝声明 <c>seams</c>（缝名数组；缺省＝null＝不声明任何缝）。
    /// <para>R1i 修订 2026-10-29：seam 恢复——缝是 handler 内部的插入点，包装可挂到 <c>(成员, 缝名)</c> 上。</para>
    /// </summary>
    /// <exception cref="PrefabLoadException">不是字符串数组，或含空白项／重复名／空数组。</exception>
    private static IReadOnlyList<string>? ReadSeams(JsonElement element, string source, string where)
    {
        if (!element.TryGetProperty("seams", out var seamsElement))
        {
            return null;
        }

        if (seamsElement.ValueKind != JsonValueKind.Array)
        {
            throw Error(PrefabErrorCategories.Structure, source, $"{where} 必须是字符串数组。");
        }

        var names = new List<string>();
        foreach (var item in seamsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Error(PrefabErrorCategories.Structure, source, $"{where} 的每一项必须是非空字符串（缝名）。");
            }

            if (names.Contains(item.GetString()!, StringComparer.Ordinal))
            {
                throw Error(
                    PrefabErrorCategories.Structure, source, $"{where} 含重复缝名 '{item.GetString()}'（缝名必须唯一）。");
            }

            names.Add(item.GetString()!);
        }

        return names.Count == 0
            ? throw Error(
                PrefabErrorCategories.Structure, source, $"{where} 不能为空数组（省略该字段＝不声明任何缝）。")
            : names;
    }

    /// <summary>
    /// 读取可选的段序声明 <c>bands</c>（有序段名数组；缺省＝null＝单段方案）。
    /// </summary>
    /// <exception cref="PrefabLoadException">不是字符串数组，或含空白项／重复名／空数组。</exception>
    private static IReadOnlyList<string>? ReadBands(JsonElement element, string source, string where)
    {
        if (!element.TryGetProperty("bands", out var bandsElement))
        {
            return null;
        }

        if (bandsElement.ValueKind != JsonValueKind.Array)
        {
            throw Error(PrefabErrorCategories.Structure, source, $"{where} 必须是字符串数组。");
        }

        var segments = new List<string>();
        foreach (var item in bandsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Error(PrefabErrorCategories.Structure, source, $"{where} 的每一项必须是非空字符串（段名）。");
            }

            segments.Add(item.GetString()!);
        }

        return segments.Count == 0
            ? throw Error(
                PrefabErrorCategories.Structure,
                source,
                $"{where} 不能为空数组（省略该字段＝单段方案：所有边同段、按声明序）。")
            : segments;
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
            if (asset.Extends is { } extends)
            {
                writer.WriteString("extends", extends);
            }

            if (asset.AuthorizesModing)
            {
                writer.WriteBoolean("authorize-moding", true);
            }

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

                // 段声明（R1i band 修订）：只在作者显式声明时写出（缺省＝单段方案，不写＝不引入噪声）。
                if (member.Bands is { } bands)
                {
                    writer.WriteStartArray("bands");
                    foreach (var segment in bands)
                    {
                        writer.WriteStringValue(segment);
                    }

                    writer.WriteEndArray();
                }

                if (member.Band is { } band)
                {
                    writer.WriteString("band", band);
                }

                // 具名缝（R1i 修订 2026-10-29：seam 恢复）：与 band 同口径——只在作者声明时写出。
                if (member.Seams is { Count: > 0 } seams)
                {
                    writer.WriteStartArray("seams");
                    foreach (var seam in seams)
                    {
                        writer.WriteStringValue(seam);
                    }

                    writer.WriteEndArray();
                }

                if (member.Sealed)
                {
                    writer.WriteBoolean("sealed", true);
                }

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

                    if (handler.Dsl is not null)
                    {
                        writer.WriteString("dsl", handler.Dsl);
                    }

                    if (handler.DslHash is not null)
                    {
                        writer.WriteString("dslHash", handler.DslHash);
                    }

                    if (handler.Generated is { } generated)
                    {
                        WriteGenerated(writer, generated);
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
                if (wrap.Seam is { } seam)
                {
                    writer.WriteString("seam", seam);
                }

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

    /// <summary>写出**来源标记**（R28／S11）：字段顺序固定，`nodeMap` 仅在非空时写出。</summary>
    private static void WriteGenerated(Utf8JsonWriter writer, HandlerGeneratedBlock generated)
    {
        writer.WriteStartObject("generated");
        writer.WriteString("generator", generated.Generator);
        writer.WriteNumber("catalogRevision", generated.CatalogRevision);
        writer.WriteString("source", generated.Source);
        writer.WriteString("dslHash", generated.DslHash);
        writer.WriteString("textHash", generated.TextHash);
        if (generated.NodeMap is { Count: > 0 } nodeMap)
        {
            writer.WriteStartObject("nodeMap");
            foreach (var pair in nodeMap.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray(pair.Key);
                writer.WriteNumberValue(pair.Value.Start);
                writer.WriteNumberValue(pair.Value.End);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
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

    /// <summary>handler 来源的清单文本（三选一互斥冲突的诊断用）。</summary>
    private static string DescribeHandlerSources(string? script, string? assemblyKey, string? dsl)
    {
        var names = new List<string>(3);
        if (script is not null)
        {
            names.Add("script");
        }

        if (assemblyKey is not null)
        {
            names.Add("assembly-key");
        }

        if (dsl is not null)
        {
            names.Add("dsl");
        }

        return string.Join("＋", names);
    }

    private static void RejectLegacyFields(JsonElement element, string source)    {
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
