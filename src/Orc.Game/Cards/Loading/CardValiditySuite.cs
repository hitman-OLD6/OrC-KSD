using System.Text.Json;
using Orc.Game.Cards.Data;
using Orc.Game.Cards.Data.Components;

namespace Orc.Game.Cards.Loading;

/// <summary>
/// **合法性检测套件**（R5/D35/D40；引擎内置、三档骨架固定）：
/// 对一张卡的元数据事实与数据体原文做逐项判定，产出「档位 ＋ 字段问题清单 ＋ 生成物」。
/// <para>
/// 判定表逐项落实 D40：
/// <list type="bullet">
///   <item><b>非法</b>（不生成卡）：<c>id</c> 缺失／<c>factionCost</c> 缺失／<c>typeCategory</c> 缺失／
///     单位 <c>battleStats</c> 缺失／指令·反制 <c>effects</c> 缺失／<c>effects</c> 引用不存在的资产／版本号非法；
///     另含 R2/D48 的 <c>id</c> 不一致、源文件缺失（含卡图文件不存在）——同属「不生成」。</item>
///   <item><b>警报</b>（可玩、按类型默认值补缺）：<c>name</c> 缺失／卡图缺失／描述缺失／<c>tagData</c> 缺失／未知词条；
///     另含 sidecar 缺失（R2：按「无元数据」判定）。</item>
/// </list>
/// </para>
/// <para>
/// 卡级聚合：**任一非法项 ⇒ 整卡非法**（D41）。非法卡不生成（<see cref="CardValidityResult.Definition"/> 为 null），
/// 警报卡以类型默认值（0／null／空串）补齐后照常生成——读取方必须容忍这些默认值。
/// </para>
/// <para>
/// 插件只能经 <see cref="ICardValidityCheck"/> **追加**检查项（D35）：追加项按同一聚合规则并档，骨架与聚合规则不可改。
/// 判定是纯函数（除显式的文件存在性探测外无 IO、无状态）。
/// </para>
/// </summary>
public sealed class CardValiditySuite
{
    private readonly IReadOnlyList<ICardValidityCheck> _extensionChecks;

    /// <summary>内置套件（无插件追加项）。</summary>
    public static CardValiditySuite BuiltIn { get; } = new();

    /// <summary>创建套件（可追加插件检查项；追加项不得改变三档骨架）。</summary>
    /// <exception cref="ArgumentNullException">追加项集合含 null。</exception>
    public CardValiditySuite(IEnumerable<ICardValidityCheck>? extensionChecks = null)
    {
        var list = new List<ICardValidityCheck>();
        if (extensionChecks is not null)
        {
            foreach (var check in extensionChecks)
            {
                ArgumentNullException.ThrowIfNull(check);
                list.Add(check);
            }
        }

        _extensionChecks = list;
    }

    /// <summary>追加的插件检查项（登记序）。</summary>
    public IReadOnlyList<ICardValidityCheck> ExtensionChecks => _extensionChecks;

    /// <summary>判定一张卡（纯函数）。</summary>
    /// <exception cref="ArgumentNullException">input 为 null。</exception>
    public CardValidityResult Evaluate(CardValidityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var issues = new List<CardFieldIssue>();

        // ── 元数据级（注册面事实） ──
        if (input.SourcePath is not null && !File.Exists(input.SourcePath))
        {
            issues.Add(CardFieldIssue.Invalid("source", "SourceFile", null, CardIssueReason.SourceMissing));
        }

        if (input.InvalidVersion is not null)
        {
            issues.Add(CardFieldIssue.Invalid("version", "AssetVersion", null, CardIssueReason.VersionInvalid));
        }

        if (!input.HasSidecar)
        {
            issues.Add(CardFieldIssue.Warning("sidecar", "AssetSidecar", null, CardIssueReason.SidecarMissing));
        }

        if (input.RawJson is null)
        {
            // 无原文：只做元数据级判定，产出回退到注册面已生成的 DTO（无 DTO ⇒ 无处可生成）。
            if (input.RegisteredDefinition is null
                && !issues.Any(issue => issue.IsInvalid)
                && !input.RegistrationIssues.Any(issue => issue.IsInvalid))
            {
                issues.Add(CardFieldIssue.Invalid("source", "CardDataBody", null, CardIssueReason.SourceMissing));
            }

            return Finish(issues, input.RegisteredDefinition, context: null, input.RegistrationIssues);
        }

        return EvaluateDataBody(input, issues);
    }

    private CardValidityResult EvaluateDataBody(CardValidityInput input, List<CardFieldIssue> issues)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input.RawJson!, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            issues.Add(CardFieldIssue.Invalid("$", "object", null, CardIssueReason.WrongType));
            return Finish(issues, null, context: null, input.RegistrationIssues);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(CardFieldIssue.Invalid("$", "object", null, CardIssueReason.WrongType));
                return Finish(issues, null, context: null, input.RegistrationIssues);
            }

            var elements = ReadComponentElements(root);
            var localId = GetString(root, "id");
            var name = GetString(root, "name");
            var description = input.Description ?? GetString(root, "description");
            var image = input.CardImagePath ?? GetString(root, "image");

            EvaluateIdentity(input, localId, name, issues);

            var components = new List<ICardDataComponentDefinition>();

            // 类别（缺失＝非法；无法归类则单位/指令/反制三类必填项无从判定）。
            TypeCategoryDefinition? typeCategory = null;
            if (!elements.TryGetValue(TypeCategoryDefinition.DefinitionName, out var typeElement))
            {
                issues.Add(CardFieldIssue.Invalid(
                    "components.typeCategory", "object", null, CardIssueReason.Missing));
            }
            else if (TryRead(TypeCategoryDefinition.Read, typeElement, out typeCategory, out var typeReason))
            {
                components.Add(typeCategory!);
            }
            else
            {
                issues.Add(CardFieldIssue.Invalid(
                    "components.typeCategory", "object", null, typeReason));
            }

            // 派系与费用（缺失＝非法：卡无法打出/计价）。
            if (!elements.TryGetValue(FactionCostDefinition.DefinitionName, out var factionElement))
            {
                issues.Add(CardFieldIssue.Invalid(
                    "components.factionCost", "object", null, CardIssueReason.Missing));
            }
            else if (TryRead(FactionCostDefinition.Read, factionElement, out var factionCost, out var factionReason))
            {
                components.Add(factionCost!);
            }
            else
            {
                issues.Add(CardFieldIssue.Invalid(
                    "components.factionCost", "object", null, factionReason));
            }

            var category = typeCategory?.Category;

            // 单位对战数值（缺失＝非法：数据缺少的单位不可玩）。
            if (string.Equals(category, CardCategory.Unit, StringComparison.Ordinal))
            {
                if (!elements.TryGetValue(BattleStatsDefinition.DefinitionName, out var statsElement))
                {
                    issues.Add(CardFieldIssue.Invalid(
                        "components.battleStats", "object", null, CardIssueReason.Missing));
                }
                else if (TryRead(BattleStatsDefinition.Read, statsElement, out var battleStats, out var statsReason))
                {
                    components.Add(battleStats!);
                }
                else
                {
                    issues.Add(CardFieldIssue.Invalid(
                        "components.battleStats", "object", null, statsReason));
                }
            }
            else if (elements.TryGetValue(BattleStatsDefinition.DefinitionName, out var statsElement))
            {
                // 非单位带对战数值：不属 D40 判定项，能读则保真带入，读不出只留警报（不影响装配）。
                if (TryRead(BattleStatsDefinition.Read, statsElement, out var battleStats, out var statsReason))
                {
                    components.Add(battleStats!);
                }
                else
                {
                    issues.Add(CardFieldIssue.Warning("components.battleStats", "object", null, statsReason));
                }
            }

            // 稀有度与标签（缺失＝警报：只影响构筑/展示）。
            if (!elements.TryGetValue(TagDataDefinition.DefinitionName, out var tagElement))
            {
                issues.Add(CardFieldIssue.Warning("components.tagData", "object", null, CardIssueReason.Missing));
                components.Add(CreateDefaultTagData());
            }
            else if (TryRead(TagDataDefinition.Read, tagElement, out var tagData, out var tagReason))
            {
                components.Add(tagData!);
            }
            else
            {
                issues.Add(CardFieldIssue.Warning("components.tagData", "object", null, tagReason));
                components.Add(CreateDefaultTagData());
            }

            // 词条（未知/未实现词条＝警报：留痕不阻断）。
            if (elements.TryGetValue(KeywordsDefinition.DefinitionName, out var keywordElement))
            {
                if (TryRead(KeywordsDefinition.Read, keywordElement, out var keywords, out var keywordReason))
                {
                    components.Add(keywords!);
                    AddUnknownKeywordIssues(keywords!, issues);
                }
                else
                {
                    issues.Add(CardFieldIssue.Warning("components.keywords", "object", null, keywordReason));
                }
            }

            // 效果（指令/反制缺失＝非法；引用不存在的资产＝非法）。
            var effectReferences = EvaluateEffects(
                input, elements, category, components, issues);

            // 卡图与描述（缺失＝警报，可玩）。
            EvaluatePresentation(input, image, description, issues);

            CardDefinition? definition = null;
            if (!issues.Any(issue => issue.IsInvalid))
            {
                try
                {
                    definition = CardDefinition.CreateForLoading(name, components);
                }
                catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
                {
                    issues.Add(CardFieldIssue.Invalid("components", "object", null, CardIssueReason.WrongType));
                }
            }

            var context = new CardValidityContext(
                input, localId, name, category, components, effectReferences);

            return Finish(issues, definition, context, input.RegistrationIssues);
        }
    }

    /// <summary>身份判定：<c>id</c> 缺失＝非法；与 sidecar 声明不一致＝非法（R2/D48）；<c>name</c> 缺失＝警报。</summary>
    private static void EvaluateIdentity(
        CardValidityInput input, string? localId, string? name, List<CardFieldIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(localId))
        {
            issues.Add(CardFieldIssue.Invalid("id", "string", "", CardIssueReason.Missing));
        }
        else if (input.MetadataLocalId is { } declared
                 && !string.Equals(declared, localId, StringComparison.Ordinal))
        {
            issues.Add(CardFieldIssue.Invalid("id", "string", localId, CardIssueReason.IdMismatch));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(CardFieldIssue.Warning("name", "string", "", CardIssueReason.Missing));
        }
    }

    /// <summary>效果判定：指令/反制缺 <c>effects</c>＝非法；<c>prefabs</c> 引用不存在的资产＝非法。</summary>
    private static IReadOnlyList<string> EvaluateEffects(
        CardValidityInput input,
        Dictionary<string, JsonElement> elements,
        string? category,
        List<ICardDataComponentDefinition> components,
        List<CardFieldIssue> issues)
    {
        var requiresEffects = string.Equals(category, CardCategory.Command, StringComparison.Ordinal)
            || string.Equals(category, CardCategory.Counter, StringComparison.Ordinal);

        if (!elements.TryGetValue(EffectsDefinition.DefinitionName, out var effectsElement))
        {
            if (requiresEffects)
            {
                issues.Add(CardFieldIssue.Invalid(
                    "components.effects", "object", null, CardIssueReason.Missing));
            }

            return Array.Empty<string>();
        }

        if (!TryRead(EffectsDefinition.Read, effectsElement, out var effects, out var effectsReason))
        {
            issues.Add(requiresEffects
                ? CardFieldIssue.Invalid("components.effects", "object", null, effectsReason)
                : CardFieldIssue.Warning("components.effects", "object", null, effectsReason));

            return Array.Empty<string>();
        }

        var parsedEffects = effects!;
        components.Add(parsedEffects);

        // 指令/反制必须真的带效果（空清单＝等同缺失：该类卡的行为载体不存在）。
        if (requiresEffects && parsedEffects.Prefabs.Count == 0 && parsedEffects.Inline.Count == 0)
        {
            issues.Add(CardFieldIssue.Invalid(
                "components.effects", "array", Array.Empty<string>(), CardIssueReason.Missing));
        }

        var references = parsedEffects.Prefabs;
        if (input.EffectReferenceExists is { } exists)
        {
            // 本卡内联的效果预制体不是独立资产（随卡分发）——引用它们不算「引用不存在的资产」。
            var inlineIds = parsedEffects.Inline
                .Select(snapshot => snapshot.Root.Id)
                .ToHashSet(StringComparer.Ordinal);

            for (var i = 0; i < references.Count; i++)
            {
                if (inlineIds.Contains(references[i]))
                {
                    continue;
                }

                if (!exists(references[i]))
                {
                    issues.Add(CardFieldIssue.Invalid(
                        $"components.effects.prefabs[{i}]",
                        "AssetReference",
                        references[i],
                        CardIssueReason.Missing));
                }
            }
        }

        return references;
    }

    /// <summary>展示面判定：卡图/描述缺失＝警报（可玩）。</summary>
    private static void EvaluatePresentation(
        CardValidityInput input, string? image, string? description, List<CardFieldIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            issues.Add(CardFieldIssue.Warning("image", "string", "", CardIssueReason.Missing));
        }
        else if (!(input.ImageFileExists ?? File.Exists)(image))
        {
            issues.Add(CardFieldIssue.Warning("image", "string", image, CardIssueReason.SourceMissing));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            issues.Add(CardFieldIssue.Warning("description", "string", "", CardIssueReason.Missing));
        }
    }

    /// <summary>未知词条判警报（逐条：路径定位到 <c>attributes</c> 下标）。</summary>
    private static void AddUnknownKeywordIssues(KeywordsDefinition keywords, List<CardFieldIssue> issues)
    {
        if (keywords.UnmappedAttributes.Count == 0)
        {
            return;
        }

        var used = new HashSet<int>();
        foreach (var unmapped in keywords.UnmappedAttributes)
        {
            var index = -1;
            for (var i = 0; i < keywords.Attributes.Count; i++)
            {
                if (!used.Contains(i) && string.Equals(keywords.Attributes[i], unmapped, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                used.Add(index);
            }

            var path = index >= 0
                ? $"components.keywords.attributes[{index}]"
                : "components.keywords.attributes";
            issues.Add(CardFieldIssue.Warning(path, "string", unmapped, CardIssueReason.Missing));
        }
    }

    /// <summary>聚合档位 ＋ 注册期问题并入 ＋ 插件追加项 ＋ 生成物收口。</summary>
    private CardValidityResult Finish(
        List<CardFieldIssue> issues,
        CardDefinition? definition,
        CardValidityContext? context,
        IReadOnlyList<CardFieldIssue> registrationIssues)
    {
        // 注册期已记录的字段问题并入（D40/R5）：与套件自身判定同「路径 ＋ 原因」时只留一条
        // （套件判定在先 ⇒ 更贴近原文，注册期记录只补其未覆盖的事实）。
        foreach (var issue in registrationIssues)
        {
            if (!issues.Any(existing => existing.Reason == issue.Reason
                && string.Equals(existing.Path, issue.Path, StringComparison.Ordinal)))
            {
                issues.Add(issue);
            }
        }

        if (context is not null)
        {
            foreach (var check in _extensionChecks)
            {
                issues.AddRange(check.Evaluate(context));
            }
        }

        var invalid = issues.Any(issue => issue.IsInvalid);
        var tier = invalid
            ? CardLoadTier.Invalid
            : issues.Count > 0 ? CardLoadTier.Warning : CardLoadTier.Valid;

        return new CardValidityResult(tier, issues, tier == CardLoadTier.Invalid ? null : definition);
    }

    /// <summary>缺失 <c>tagData</c> 的补全组件（稀有度取内置默认值 <c>standard</c>、无开放 tag）。</summary>
    private static TagDataDefinition CreateDefaultTagData()
        => new(Rarity.Standard, Array.Empty<string>());

    /// <summary>读 <c>components</c> 数组为「组件名（小写）→ 元素」表（重复项取首条；非对象/无名项跳过）。</summary>
    private static Dictionary<string, JsonElement> ReadComponentElements(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetProperty(root, "components", out var components) || components.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var element in components.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(element, "component");
            if (!string.IsNullOrWhiteSpace(name))
            {
                map.TryAdd(name!, element);
            }
        }

        return map;
    }

    /// <summary>以既有组件 reader 解析（失败＝false ＋ 原因；不抛）。</summary>
    private static bool TryRead<T>(
        Func<JsonElement, T> reader, JsonElement element, out T? value, out CardIssueReason reason)
        where T : class, ICardDataComponentDefinition
    {
        value = null;
        reason = CardIssueReason.WrongType;

        try
        {
            value = reader(element);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            reason = CardIssueReason.OutOfRange;
            return false;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException
            or InvalidOperationException or InvalidDataException)
        {
            reason = CardIssueReason.WrongType;
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
