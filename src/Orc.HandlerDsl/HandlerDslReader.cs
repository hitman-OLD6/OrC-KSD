using System.Text.Json;
using Orc.Core;

namespace Orc.HandlerDsl;

/// <summary>读取结果：成功携带文档，失败携带**全部**错误（收集齐而非遇错即停）。</summary>
public sealed record HandlerDslReadResult(DslDocument? Document, IReadOnlyList<HandlerDslError> Errors)
{
    /// <summary>是否无错误。</summary>
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// DSL 文档的读取与**结构校验**（R18／R13／R21／D17）：JSON → <see cref="DslDocument"/>，
/// 同时把「不是树」「字段封闭表」「<c>else</c> 省略」「<c>bind</c> 双形态」「未知节点／引脚」等结构问题
/// 收成封闭错误码。
/// <para>
/// 分工：本类只做**语法与结构**（含目录引脚名／必填性／bind 形态）；**类型推导与引脚类型判定**
/// 在 <see cref="HandlerDslTypeChecker"/>；作用域与可写性等语义规则在后续校验器里补齐。
/// </para>
/// </summary>
public static class HandlerDslReader
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,

        // 放开 JSON 读取器自身的深度限制：**契约的 64 层上限由本类判定**（graph-shape 带节点路径），
        // 不能让解析器先抛一个只有字节位置、没有节点路径的异常。
        MaxDepth = 256,
    };

    private static readonly Dictionary<string, HashSet<string>> BuiltinFields = new(StringComparer.Ordinal)
    {
        ["sequence"] = new(StringComparer.Ordinal) { "op", "actions" },
        ["branch"] = new(StringComparer.Ordinal) { "op", "test", "then", "else" },
        ["loop"] = new(StringComparer.Ordinal) { "op", "times", "actions" },
        ["while"] = new(StringComparer.Ordinal) { "op", "test", "limit", "actions" },
        ["iterate"] = new(StringComparer.Ordinal) { "op", "over", "as", "actions" },
        ["choice"] = new(StringComparer.Ordinal) { "op", "prompt", "options" },
        ["let"] = new(StringComparer.Ordinal) { "op", "name", "value" },
        ["write"] = new(StringComparer.Ordinal) { "op", "field", "value" },
        ["ask"] = new(StringComparer.Ordinal)
            { "op", "kind", "side", "zone", "filter", "prompt", "bind", "fallback" },
        ["script"] = new(StringComparer.Ordinal) { "op", "inputs", "outputs", "body" },
    };

    private static readonly HashSet<string> OptionFields = new(StringComparer.Ordinal) { "label", "actions" };

    private static readonly HashSet<string> RootFields =
        new(StringComparer.Ordinal) { "schemaVersion", "catalogRevision", "actions" };

    private static readonly HashSet<string> AskKinds = new(StringComparer.Ordinal) { "unit", "hq", "card" };

    private static readonly HashSet<string> ReadTargets = new(StringComparer.Ordinal) { "self", "viewer" };

    /// <summary>
    /// 读取一份 DSL 文档。
    /// </summary>
    /// <param name="json">文档 JSON 文本。</param>
    /// <param name="catalog">节点目录（动作节点的引脚集由它给出；null＝无法解析动作节点）。</param>
    public static HandlerDslReadResult Read(string json, IHandlerDslCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        var errors = new List<HandlerDslError>();
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, HandlerDslContract.RootPath,
                $"不是合法 JSON：{ex.Message}"));
            return new HandlerDslReadResult(null, errors);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Error(HandlerDslErrorCodes.GraphShape, HandlerDslContract.RootPath,
                    "文档必须是对象（树形结构；R18 不接受其它形态）。"));
                return new HandlerDslReadResult(null, errors);
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!RootFields.Contains(property.Name))
                {
                    errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{HandlerDslContract.RootPath}.{property.Name}",
                        $"未知的文档字段 '{property.Name}'：文档只接受 schemaVersion／catalogRevision／actions"
                        + "（资产 id、成员名、挂载地址、相位不属于 DSL，R13）。"));
                }
            }

            if (!TryReadSchemaVersion(root, errors, out var schemaVersion))
            {
                return new HandlerDslReadResult(null, errors);
            }

            var catalogRevision = ReadCatalogRevision(root, errors);

            IReadOnlyList<DslNode> actions;
            if (!root.TryGetProperty("actions", out var actionsElement))
            {
                errors.Add(Error(HandlerDslErrorCodes.GraphShape, $"{HandlerDslContract.RootPath}.actions",
                    "文档缺少 actions：handler 的根执行序列是必填的（空 handler 用空数组表示）。"));
                return new HandlerDslReadResult(null, errors);
            }

            var parsedActions = ParseRootActions(actionsElement, catalog, errors);
            if (parsedActions is null)
            {
                return new HandlerDslReadResult(null, errors);
            }

            actions = parsedActions;

            if (catalog is not null && catalogRevision is not null && catalogRevision.Value != catalog.Revision)
            {
                // 只作诊断、绝不单独构成拒绝理由（SPEC §12.7）：仅在已有错误上留痕。
                AppendRevisionNote(errors, catalogRevision, catalog.Revision);
            }

            return new HandlerDslReadResult(new DslDocument(schemaVersion, catalogRevision, actions), errors);
        }
    }

    private static bool TryReadSchemaVersion(JsonElement root, List<HandlerDslError> errors, out int version)
    {
        version = 0;
        var path = $"{HandlerDslContract.RootPath}.schemaVersion";

        if (!root.TryGetProperty("schemaVersion", out var element))
        {
            errors.Add(Error(HandlerDslErrorCodes.SchemaVersion, path,
                $"文档缺少 schemaVersion；当前契约版本 {HandlerDslContract.SchemaVersion}。"));
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out version))
        {
            errors.Add(Error(HandlerDslErrorCodes.SchemaVersion, path,
                $"schemaVersion 必须是整数；当前契约版本 {HandlerDslContract.SchemaVersion}。"));
            return false;
        }

        if (version != HandlerDslContract.SchemaVersion)
        {
            errors.Add(Error(HandlerDslErrorCodes.SchemaVersion, path,
                $"schemaVersion 不匹配：文档 {version}，当前实现 {HandlerDslContract.SchemaVersion}"
                + "（硬轴：不匹配即拒绝，不做迁移）。"));
            return false;
        }

        return true;
    }

    private static int? ReadCatalogRevision(JsonElement root, List<HandlerDslError> errors)
    {
        if (!root.TryGetProperty("catalogRevision", out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var revision))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{HandlerDslContract.RootPath}.catalogRevision",
                "catalogRevision 必须是整数（缺省合法）。"));
            return null;
        }

        return revision;
    }

    private static void AppendRevisionNote(List<HandlerDslError> errors, int? documentRevision, int currentRevision)
    {
        if (documentRevision is null || errors.Count == 0)
        {
            return;
        }

        var note = HandlerDslContract.CatalogRevisionNote(documentRevision, currentRevision);
        for (var i = 0; i < errors.Count; i++)
        {
            errors[i] = errors[i] with { Message = errors[i].Message + note };
        }
    }

    // ── 节点 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 根 <c>actions</c>：字段级错误报 <c>$.actions</c>；节点路径仍以 <c>actions[i]</c> 起头。
    /// </summary>
    private static IReadOnlyList<DslNode>? ParseRootActions(
        JsonElement element, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, $"{HandlerDslContract.RootPath}.actions",
                $"actions 必须是数组（实为 {Describe(element.ValueKind)}）：DSL 是树，数组顺序即执行顺序。"));
            return null;
        }

        return ParseActions(element, "actions", 1, catalog, errors);
    }

    private static IReadOnlyList<DslNode>? ParseActions(
        JsonElement element,
        string path,
        int depth,
        IHandlerDslCatalog? catalog,
        List<HandlerDslError> errors)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, path,
                $"子节点必须是数组（实为 {Describe(element.ValueKind)}）：DSL 是树，数组顺序即执行顺序。"));
            return null;
        }

        var nodes = new List<DslNode>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var node = ParseNode(item, $"{path}[{index}]", depth, catalog, errors);
            if (node is not null)
            {
                nodes.Add(node);
            }

            index++;
        }

        return nodes;
    }

    private static DslNode? ParseNode(
        JsonElement element,
        string path,
        int depth,
        IHandlerDslCatalog? catalog,
        List<HandlerDslError> errors)
    {
        if (depth > HandlerDslContract.MaxDepth)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, path,
                $"嵌套深度超过上限 {HandlerDslContract.MaxDepth}（与既有 chain 深度口径一致）。"));
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, path,
                $"节点必须是对象（实为 {Describe(element.ValueKind)}）。"));
            return null;
        }

        if (!element.TryGetProperty("op", out var opElement) || opElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.NodeUnknown, path,
                "节点缺少字符串字段 op（op 取值域＝10 个内建 op ∪ 目录节点名）。"));
            return null;
        }

        var op = opElement.GetString()!;
        if (!HandlerDslContract.IsBuiltinOp(op))
        {
            return ParseActionNode(element, path, op, catalog, errors);
        }

        CheckFields(element, path, BuiltinFields[op], errors);

        return op switch
        {
            "sequence" => ParseSequence(element, path, depth, catalog, errors),
            "branch" => ParseBranch(element, path, depth, catalog, errors),
            "loop" => ParseLoop(element, path, depth, catalog, errors),
            "while" => ParseWhile(element, path, depth, catalog, errors),
            "iterate" => ParseIterate(element, path, depth, catalog, errors),
            "choice" => ParseChoice(element, path, depth, catalog, errors),
            "let" => ParseLet(element, path, catalog, errors),
            "write" => ParseWrite(element, path, catalog, errors),
            "ask" => ParseAsk(element, path, catalog, errors),
            "script" => ParseScript(element, path, catalog, errors),
            _ => null,
        };
    }

    private static DslNode? ParseSequence(
        JsonElement element, string path, int depth, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var actions = RequireActions(element, "actions", path, depth, catalog, errors, nonEmpty: false);
        return actions is null ? null : new SequenceNode(path, actions);
    }

    private static DslNode? ParseBranch(
        JsonElement element, string path, int depth, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var test = RequireExpression(element, "test", path, catalog, errors);
        var then = RequireActions(element, "then", path, depth, catalog, errors, nonEmpty: true, missingIsGraphShape: true);
        var elseActions = OptionalActions(element, "else", path, depth, catalog, errors, out var elsePresent);

        if (test is null || then is null || (elsePresent && elseActions is null))
        {
            return null;
        }

        return new BranchNode(path, test, then, elseActions);
    }

    private static DslNode? ParseLoop(
        JsonElement element, string path, int depth, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var times = RequireLoopBound(element, "times", path, catalog, errors);
        var actions = RequireActions(element, "actions", path, depth, catalog, errors, nonEmpty: false);
        return times is null || actions is null ? null : new LoopNode(path, times, actions);
    }

    private static DslNode? ParseWhile(
        JsonElement element, string path, int depth, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var test = RequireExpression(element, "test", path, catalog, errors);
        var limit = RequireLoopBound(element, "limit", path, catalog, errors);
        var actions = RequireActions(element, "actions", path, depth, catalog, errors, nonEmpty: false);

        if (test is null || limit is null || actions is null)
        {
            return null;
        }

        return new WhileNode(path, test, limit, actions);
    }

    private static DslNode? ParseIterate(
        JsonElement element, string path, int depth, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var over = RequireExpression(element, "over", path, catalog, errors);
        var bindingPath = $"{path}.as";
        var asName = RequireString(element, "as", bindingPath, errors);
        if (asName is not null && !IsValidBindingName(asName))
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, bindingPath,
                $"绑定名 '{asName}' 非法：只允许 [A-Za-z_][A-Za-z0-9_]*。"));
            asName = null;
        }

        var actions = RequireActions(element, "actions", path, depth, catalog, errors, nonEmpty: false);
        if (over is null || asName is null || actions is null)
        {
            return null;
        }

        return new IterateNode(path, over, asName, actions);
    }

    private static DslNode? ParseChoice(
        JsonElement element, string path, int depth, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var promptPath = $"{path}.prompt";
        var prompt = RequireString(element, "prompt", promptPath, errors);
        var optionsPath = $"{path}.options";

        if (!element.TryGetProperty("options", out var optionsElement))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, optionsPath, "choice 缺少 options（必填，非空）。"));
            return null;
        }

        if (optionsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, optionsPath,
                $"options 必须是数组（实为 {Describe(optionsElement.ValueKind)}）。"));
            return null;
        }

        var options = new List<ChoiceOption>();
        var index = 0;
        foreach (var item in optionsElement.EnumerateArray())
        {
            var optionPath = $"{optionsPath}[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Error(HandlerDslErrorCodes.GraphShape, optionPath, "选项必须是对象。"));
                index++;
                continue;
            }

            CheckFields(item, optionPath, OptionFields, errors);
            var label = RequireString(item, "label", $"{optionPath}.label", errors);
            IReadOnlyList<DslNode>? actions;
            if (!item.TryGetProperty("actions", out var actionsElement))
            {
                errors.Add(Error(HandlerDslErrorCodes.PinMissing, $"{optionPath}.actions",
                    "选项缺少 actions（必填；空动作请给空数组）。"));
                actions = null;
            }
            else
            {
                actions = ParseActions(actionsElement, $"{optionPath}.actions", depth + 1, catalog, errors);
            }

            if (label is not null && actions is not null)
            {
                options.Add(new ChoiceOption(label, actions, optionPath));
            }

            index++;
        }

        if (options.Count == 0)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, optionsPath, "choice 的 options 不得为空。"));
            return null;
        }

        return prompt is null ? null : new ChoiceNode(path, prompt, options);
    }

    private static DslNode? ParseLet(
        JsonElement element, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var namePath = $"{path}.name";
        var name = RequireString(element, "name", namePath, errors);
        if (name is not null && !IsValidBindingName(name))
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, namePath,
                $"绑定名 '{name}' 非法：只允许 [A-Za-z_][A-Za-z0-9_]*。"));
            name = null;
        }

        var value = RequireExpression(element, "value", path, catalog, errors);
        return name is null || value is null ? null : new LetNode(path, name, value);
    }

    private static DslNode? ParseWrite(
        JsonElement element, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var fieldPath = $"{path}.field";
        var field = RequireString(element, "field", fieldPath, errors);
        var value = RequireExpression(element, "value", path, catalog, errors);
        return field is null || value is null ? null : new WriteNode(path, field, value);
    }

    private static DslNode? ParseAsk(
        JsonElement element, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var kindPath = $"{path}.kind";
        var kind = RequireString(element, "kind", kindPath, errors);
        if (kind is not null && !AskKinds.Contains(kind))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, kindPath,
                $"ask.kind '{kind}' 不在封闭集内：只允许 unit／hq／card。"));
            kind = null;
        }

        var prompt = RequireString(element, "prompt", $"{path}.prompt", errors);

        string? side = null;
        if (element.TryGetProperty("side", out var sideElement))
        {
            if (sideElement.ValueKind != JsonValueKind.String)
            {
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{path}.side", "side 必须是字符串。"));
            }
            else
            {
                side = sideElement.GetString();
            }
        }

        string? zone = null;
        if (element.TryGetProperty("zone", out var zoneElement))
        {
            if (zoneElement.ValueKind != JsonValueKind.String)
            {
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{path}.zone", "zone 必须是字符串。"));
            }
            else
            {
                zone = zoneElement.GetString();
            }
        }

        DslExpression? filter = null;
        if (element.TryGetProperty("filter", out var filterElement))
        {
            filter = ParseExpression(filterElement, $"{path}.filter", catalog, errors);
        }

        DslBind? bind = null;
        if (element.TryGetProperty("bind", out var bindElement))
        {
            bind = ParseSingleBind(bindElement, $"{path}.bind", errors);
        }

        var fallback = AskFallback.Empty;
        if (element.TryGetProperty("fallback", out var fallbackElement))
        {
            var text = fallbackElement.ValueKind == JsonValueKind.String ? fallbackElement.GetString() : null;
            switch (text)
            {
                case "empty":
                    fallback = AskFallback.Empty;
                    break;
                case "first":
                    fallback = AskFallback.First;
                    break;
                default:
                    errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{path}.fallback",
                        "ask.fallback 只允许 empty／first（缺省 empty）；它只管「无交互通道」这一种情况。"));
                    break;
            }
        }

        if (kind is null || prompt is null)
        {
            return null;
        }

        return new AskNode(path, kind, prompt, side, zone, filter, bind, fallback);
    }

    private static DslNode? ParseScript(
        JsonElement element, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var inputs = ParseScriptPins(element, "inputs", path, errors);
        var outputs = ParseScriptPins(element, "outputs", path, errors);

        var bodyPath = $"{path}.body";
        if (!element.TryGetProperty("body", out var bodyElement))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, bodyPath, "script 缺少 body（必填）。"));
            return null;
        }

        if (bodyElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, bodyPath, "script.body 必须是字符串（csx 片段）。"));
            return null;
        }

        if (inputs is null || outputs is null)
        {
            return null;
        }

        return new ScriptNode(path, inputs, outputs, bodyElement.GetString()!);
    }

    private static IReadOnlyList<DslScriptPin>? ParseScriptPins(
        JsonElement element, string field, string path, List<HandlerDslError> errors)
    {
        var pinsPath = $"{path}.{field}";
        if (!element.TryGetProperty(field, out var pinsElement))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, pinsPath,
                $"script 缺少 {field}（必填；无引脚请给空数组——脚本只经声明引脚与图交互）。"));
            return null;
        }

        if (pinsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, pinsPath,
                $"{field} 必须是数组（实为 {Describe(pinsElement.ValueKind)}）。"));
            return null;
        }

        var pins = new List<DslScriptPin>();
        var index = 0;
        var ok = true;
        foreach (var item in pinsElement.EnumerateArray())
        {
            var pinPath = $"{pinsPath}[{index}]";
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Error(HandlerDslErrorCodes.GraphShape, pinPath, "引脚必须是对象。"));
                ok = false;
                continue;
            }

            var name = RequireString(item, "name", $"{pinPath}.name", errors);
            if (name is not null && !IsValidBindingName(name))
            {
                errors.Add(Error(HandlerDslErrorCodes.BindScope, $"{pinPath}.name",
                    $"引脚名 '{name}' 非法：只允许 [A-Za-z_][A-Za-z0-9_]*。"));
                name = null;
            }

            var typePath = $"{pinPath}.type";
            var typeText = RequireString(item, "type", typePath, errors);
            PinType? type = null;
            if (typeText is not null)
            {
                if (PinType.TryParse(typeText, out var parsedType))
                {
                    type = parsedType;
                }
                else
                {
                    errors.Add(Error(HandlerDslErrorCodes.PinUnknown, typePath,
                        $"引脚类型 '{typeText}' 超出封闭集：只允许 {string.Join("／", PinType.CanonicalNames)}。"));
                    ok = false;
                }
            }

            if (name is not null && type is not null)
            {
                pins.Add(new DslScriptPin(name, type, pinPath));
            }
            else
            {
                ok = false;
            }
        }

        return ok ? pins : null;
    }

    private static DslNode? ParseActionNode(
        JsonElement element, string path, string op, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        if (catalog is null)
        {
            errors.Add(Error(HandlerDslErrorCodes.NodeUnknown, path,
                $"未知节点 '{op}'：未提供目录，无法解析动作节点（op 取值域＝10 个内建 op ∪ 目录节点名）。"));
            return null;
        }

        if (!catalog.TryGetNode(op, out var signature))
        {
            errors.Add(Error(HandlerDslErrorCodes.NodeUnknown, path,
                $"未知节点 '{op}'：不在 10 个内建 op 内，也不在目录（catalogRevision={catalog.Revision}）里。"
                + RenameHint(catalog, op)));
            return null;
        }

        var arguments = new List<DslArgument>();
        DslBind? bind = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == "op")
            {
                continue;
            }

            var fieldPath = $"{path}.{property.Name}";
            if (property.Name == "bind")
            {
                bind = ParseBind(property.Value, fieldPath, signature, errors);
                continue;
            }

            var pin = signature.Input(property.Name);
            if (pin is null)
            {
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, fieldPath,
                    $"节点 '{op}' 没有引脚 '{property.Name}'；可用引脚："
                    + $"{string.Join("／", signature.Inputs.Select(p => p.Name))}。"));
                continue;
            }

            var expression = ParseExpression(property.Value, fieldPath, catalog, errors);
            if (expression is not null)
            {
                arguments.Add(new DslArgument(pin.Name, expression));
            }
        }

        foreach (var pin in signature.Inputs)
        {
            if (pin.Required && arguments.All(a => !string.Equals(a.Name, pin.Name, StringComparison.Ordinal)))
            {
                errors.Add(Error(HandlerDslErrorCodes.PinMissing, $"{path}.{pin.Name}",
                    $"节点 '{op}' 的必填引脚 '{pin.Name}'（{pin.Type.Format()}）未连接。"));
            }
        }

        return new ActionNode(path, op, arguments, bind);
    }

    private static DslBind? ParseBind(
        JsonElement element, string path, DslNodeSignature signature, List<HandlerDslError> errors)
    {
        if (signature.Outputs.Count == 0)
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                $"节点 '{signature.Name}' 没有返回值，不能写 bind（不写 bind ＝忽略返回值）。"));
            return null;
        }

        if (signature.Outputs.Count == 1)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                    $"节点 '{signature.Name}' 只有一个输出 '{signature.Outputs[0].Name}'，bind 必须写成字符串"
                    + "（多输出才用对象；同一语义只许一种文本，SPEC §8.9）。"));
                return null;
            }

            var name = element.GetString()!;
            if (!IsValidBindingName(name))
            {
                errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                    $"绑定名 '{name}' 非法：只允许 [A-Za-z_][A-Za-z0-9_]*。"));
                return null;
            }

            return DslBind.SingleBind(name, path);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                $"节点 '{signature.Name}' 有 {signature.Outputs.Count} 个输出，bind 必须是对象"
                + $"（键集恰好等于：{string.Join("／", signature.Outputs.Select(o => o.Name))}）。"));
            return null;
        }

        var map = new List<KeyValuePair<string, string>>();
        var ok = true;
        foreach (var property in element.EnumerateObject())
        {
            if (signature.Output(property.Name) is null)
            {
                errors.Add(Error(HandlerDslErrorCodes.BindScope, $"{path}.{property.Name}",
                    $"未知输出名 '{property.Name}'；可用输出："
                    + $"{string.Join("／", signature.Outputs.Select(o => o.Name))}。"));
                ok = false;
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add(Error(HandlerDslErrorCodes.BindScope, $"{path}.{property.Name}",
                    "输出绑定值必须是字符串（绑定名）。"));
                ok = false;
                continue;
            }

            var name = property.Value.GetString()!;
            if (!IsValidBindingName(name))
            {
                errors.Add(Error(HandlerDslErrorCodes.BindScope, $"{path}.{property.Name}",
                    $"绑定名 '{name}' 非法：只允许 [A-Za-z_][A-Za-z0-9_]*。"));
                ok = false;
                continue;
            }

            map.Add(new KeyValuePair<string, string>(property.Name, name));
        }

        foreach (var output in signature.Outputs)
        {
            if (map.All(pair => !string.Equals(pair.Key, output.Name, StringComparison.Ordinal)))
            {
                errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                    $"bind 缺少输出 '{output.Name}'：多输出形态的键集必须**恰好等于**目录声明的输出名集合。"));
                ok = false;
            }
        }

        return ok ? DslBind.MultipleBind(map, path) : null;
    }

    private static DslBind? ParseSingleBind(JsonElement element, string path, List<HandlerDslError> errors)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                "ask 的结果是单个 list<entity>，bind 必须写成字符串。"));
            return null;
        }

        var name = element.GetString()!;
        if (!IsValidBindingName(name))
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                $"绑定名 '{name}' 非法：只允许 [A-Za-z_][A-Za-z0-9_]*。"));
            return null;
        }

        return DslBind.SingleBind(name, path);
    }

    // ── 表达式 ──────────────────────────────────────────────────────────────

    private static DslExpression? ParseExpression(
        JsonElement element, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, path,
                $"引脚取值必须是对象且为五种变体之一（lit／var／view／read／op；实为 {Describe(element.ValueKind)}）。"));
            return null;
        }

        string? variant = null;
        var variantElement = default(JsonElement);
        var argsElement = default(JsonElement);
        var hasArgs = false;
        var unknownFieldReported = false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "lit" or "var" or "view" or "read" or "op")
            {
                if (variant is not null)
                {
                    errors.Add(Error(HandlerDslErrorCodes.PinUnknown, path,
                        $"表达式只能有一个变体键（同时出现 '{variant}' 与 '{property.Name}'）。"));
                    return null;
                }

                variant = property.Name;
                variantElement = property.Value;
            }
            else if (property.Name == "args")
            {
                argsElement = property.Value;
                hasArgs = true;
            }
            else
            {
                unknownFieldReported = true;
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{path}.{property.Name}",
                    $"未知的表达式字段 '{property.Name}'：只允许 lit／var／view／read／op（args 只属于 op 变体）。"));
            }
        }

        if (variant is null)
        {
            if (!unknownFieldReported)
            {
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, path,
                    "表达式缺少变体键：必须恰有一个（lit／var／view／read／op）。"));
            }

            return null;
        }

        if (variant != "op" && hasArgs)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{path}.args",
                $"只有算子（op）变体有 args；'{variant}' 不接受 args。"));
        }

        return variant switch
        {
            "lit" => ParseLiteral(variantElement, path, errors),
            "var" => ParseVar(variantElement, path, errors),
            "view" => ParseView(variantElement, path, errors),
            "read" => ParseRead(variantElement, path, catalog, errors),
            _ => ParseOp(variantElement, argsElement, hasArgs, path, catalog, errors),
        };
    }

    private static DslExpression? ParseLiteral(JsonElement element, string path, List<HandlerDslError> errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return LiteralExpression.NumberLiteral(path, element.GetDouble(), element.GetRawText());
            case JsonValueKind.String:
                return LiteralExpression.StringLiteral(path, element.GetString()!);
            case JsonValueKind.True:
                return LiteralExpression.BooleanLiteral(path, true);
            case JsonValueKind.False:
                return LiteralExpression.BooleanLiteral(path, false);
            default:
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, path,
                    $"lit 只接受 number／string／boolean（实为 {Describe(element.ValueKind)}）。"));
                return null;
        }
    }

    private static DslExpression? ParseVar(JsonElement element, string path, List<HandlerDslError> errors)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, path, "var 的取值必须是绑定名字符串。"));
            return null;
        }

        var name = element.GetString()!;
        if (!IsValidBindingName(name))
        {
            errors.Add(Error(HandlerDslErrorCodes.BindScope, path,
                $"绑定名 '{name}' 非法：只允许 [A-Za-z_][A-Za-z0-9_]*。"));
            return null;
        }

        return new VarExpression(path, name);
    }

    private static DslExpression? ParseView(JsonElement element, string path, List<HandlerDslError> errors)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(element.GetString()))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, path, "view 的取值必须是视图字段名（非空字符串）。"));
            return null;
        }

        return new ViewExpression(path, element.GetString()!);
    }

    private static DslExpression? ParseRead(
        JsonElement element, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, path,
                "read 必须是对象 { \"of\": …, \"path\": … }。"));
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not ("of" or "path"))
            {
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{path}.read.{property.Name}",
                    $"未知的 read 字段 '{property.Name}'：只允许 of／path。"));
            }
        }

        var ofPath = $"{path}.read.of";
        var propPath = $"{path}.read.path";

        if (!element.TryGetProperty("of", out var ofElement) || ofElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, ofPath, "read 缺少 of（取值集：self／viewer）。"));
            return null;
        }

        var of = ofElement.GetString()!;
        if (!ReadTargets.Contains(of))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, ofPath,
                $"read.of '{of}' 超出封闭集：只允许 self／viewer。"));
            return null;
        }

        if (!element.TryGetProperty("path", out var propertyElement) || propertyElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, propPath, "read 缺少 path（可读项由目录声明）。"));
            return null;
        }

        var readProperty = propertyElement.GetString()!;
        if (string.IsNullOrEmpty(readProperty))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, propPath, "read.path 不得为空。"));
            return null;
        }

        if (catalog is not null && !catalog.Readables.ContainsKey(readProperty))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, propPath,
                $"read.path '{readProperty}' 不在目录声明的可读项内（可用："
                + $"{string.Join("／", catalog.Readables.Keys)}）。"));
            return null;
        }

        return new ReadExpression(path, of, readProperty);
    }

    private static DslExpression? ParseOp(
        JsonElement opElement,
        JsonElement argsElement,
        bool hasArgs,
        string path,
        IHandlerDslCatalog? catalog,
        List<HandlerDslError> errors)
    {
        if (opElement.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, path, "算子名必须是字符串。"));
            return null;
        }

        var op = opElement.GetString()!;
        var argsPath = $"{path}.args";

        if (!hasArgs)
        {
            errors.Add(Error(HandlerDslErrorCodes.ExprArity, argsPath, $"算子 '{op}' 缺少 args（必填，非空数组）。"));
            return null;
        }

        if (argsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(HandlerDslErrorCodes.ExprArity, argsPath,
                $"算子 '{op}' 的 args 必须是数组（实为 {Describe(argsElement.ValueKind)}）。"));
            return null;
        }

        if (argsElement.GetArrayLength() == 0)
        {
            errors.Add(Error(HandlerDslErrorCodes.ExprArity, argsPath, $"算子 '{op}' 的 args 不得为空。"));
            return null;
        }

        var args = new List<DslExpression>();
        var index = 0;
        foreach (var item in argsElement.EnumerateArray())
        {
            var arg = ParseExpression(item, $"{argsPath}[{index}]", catalog, errors);
            if (arg is not null)
            {
                args.Add(arg);
            }

            index++;
        }

        return new OpExpression(path, op, args);
    }

    // ── 通用小工具 ───────────────────────────────────────────────────────────

    private static DslExpression? RequireExpression(
        JsonElement parent, string field, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var fieldPath = $"{path}.{field}";
        if (!parent.TryGetProperty(field, out var element))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, fieldPath,
                $"节点缺少 {field}（必填：值引脚未连接）。"));
            return null;
        }

        return ParseExpression(element, fieldPath, catalog, errors);
    }

    private static DslExpression? RequireLoopBound(
        JsonElement parent, string field, string path, IHandlerDslCatalog? catalog, List<HandlerDslError> errors)
    {
        var fieldPath = $"{path}.{field}";
        if (!parent.TryGetProperty(field, out var element))
        {
            errors.Add(Error(HandlerDslErrorCodes.LoopUnbounded, fieldPath,
                $"循环缺少 {field}：上限必填、可为任意 number 表达式（不落默认值兜底）。"));
            return null;
        }

        return ParseExpression(element, fieldPath, catalog, errors);
    }

    private static string? RequireString(JsonElement parent, string field, string fieldPath, List<HandlerDslError> errors)
    {
        if (!parent.TryGetProperty(field, out var element))
        {
            errors.Add(Error(HandlerDslErrorCodes.PinMissing, fieldPath, $"缺少 {field}（必填）。"));
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(HandlerDslErrorCodes.PinUnknown, fieldPath,
                $"{field} 必须是字符串（实为 {Describe(element.ValueKind)}）。"));
            return null;
        }

        return element.GetString();
    }

    private static IReadOnlyList<DslNode>? RequireActions(
        JsonElement parent,
        string field,
        string path,
        int depth,
        IHandlerDslCatalog? catalog,
        List<HandlerDslError> errors,
        bool nonEmpty,
        bool missingIsGraphShape = false)
    {
        var fieldPath = $"{path}.{field}";
        if (!parent.TryGetProperty(field, out var element))
        {
            // `then` 缺失＝graph-shape（D17 把「then 缺失或空」归入该码）；其余容器的 actions 缺失＝pin-missing。
            errors.Add(missingIsGraphShape
                ? Error(HandlerDslErrorCodes.GraphShape, fieldPath, $"branch 缺少 {field}（必填且非空）。")
                : Error(HandlerDslErrorCodes.PinMissing, fieldPath, $"缺少 {field}（必填；无子节点请给空数组）。"));
            return null;
        }

        var nodes = ParseActions(element, fieldPath, depth + 1, catalog, errors);
        if (nodes is null)
        {
            return null;
        }

        if (nonEmpty && nodes.Count == 0)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, fieldPath, $"{field} 不得为空。"));
            return null;
        }

        return nodes;
    }

    private static IReadOnlyList<DslNode>? OptionalActions(
        JsonElement parent,
        string field,
        string path,
        int depth,
        IHandlerDslCatalog? catalog,
        List<HandlerDslError> errors,
        out bool present)
    {
        present = parent.TryGetProperty(field, out var element);
        if (!present)
        {
            // 无 else ＝省略字段（**不用**空数组：同一语义只许一种文本，SPEC §3）。
            return null;
        }

        var fieldPath = $"{path}.{field}";
        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, fieldPath,
                $"{field} 必须是数组（实为 {Describe(element.ValueKind)}）。"));
            return null;
        }

        if (element.GetArrayLength() == 0)
        {
            errors.Add(Error(HandlerDslErrorCodes.GraphShape, fieldPath,
                $"\"{field}\": [] 被拒绝：无 {field} 必须省略该字段（否则同一语义有两种文本）。"));
            return null;
        }

        return ParseActions(element, fieldPath, depth + 1, catalog, errors);
    }

    private static void CheckFields(
        JsonElement element, string path, IReadOnlySet<string> allowed, List<HandlerDslError> errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                errors.Add(Error(HandlerDslErrorCodes.PinUnknown, $"{path}.{property.Name}",
                    $"节点没有字段 '{property.Name}'；可用字段：{string.Join("／", allowed)}。"));
            }
        }
    }

    private static bool IsValidBindingName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // `__` 是**生成器保留前缀**（`__runtime`／`__bound_*`／`__res_*`／`__i*`／`__choice*`）。
        // 作者若用同名绑定，生成的 csx 里会出现重复声明——那是编译错误，不是图错误，
        // 报出来读者根本对不上；所以在图校验期就挡住（SPEC §8）。
        if (name.StartsWith("__", StringComparison.Ordinal))
        {
            return false;
        }

        if (name[0] != '_' && !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        return name.All(c => c == '_' || char.IsAsciiLetterOrDigit(c));
    }

    /// <summary>
    /// 「疑似改名」提示（D12／SPEC §12.2）：**只提示、不改判定**——仍然是 <c>node-unknown</c> 拒绝，
    /// 不猜测、不自动迁移（R7 的不反推精神）。候选：目录节点的名字去掉 <c>Async</c>／忽略大小写后与未知 op 相同，
    /// 或某个目录方法的原名就是它。候选恰好一个才提示。
    /// </summary>
    private static string RenameHint(IHandlerDslCatalog catalog, string op)
    {
        var candidates = new List<string>();
        foreach (var node in catalog.Nodes)
        {
            var sameAsDerived = string.Equals(HandlerDslNaming.DeriveNodeName(op), node.Name, StringComparison.Ordinal);
            var sameIgnoringCase = string.Equals(op, node.Name, StringComparison.OrdinalIgnoreCase);
            var sameAsMethod = string.Equals(op, node.Method, StringComparison.Ordinal);
            if ((sameAsDerived || sameIgnoringCase || sameAsMethod) && !candidates.Contains(node.Name, StringComparer.Ordinal))
            {
                candidates.Add(node.Name);
            }
        }

        return candidates.Count == 1
            ? $"（疑似改名：'{op}' → '{candidates[0]}'；仅为提示，不做自动迁移）"
            : string.Empty;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "对象",
        JsonValueKind.Array => "数组",
        JsonValueKind.String => "字符串",
        JsonValueKind.Number => "数字",
        JsonValueKind.True or JsonValueKind.False => "布尔",
        JsonValueKind.Null => "null",
        _ => kind.ToString(),
    };

    private static HandlerDslError Error(string code, string nodePath, string message) =>
        new(code, nodePath, message);
}
