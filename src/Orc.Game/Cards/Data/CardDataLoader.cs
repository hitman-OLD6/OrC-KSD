using System.Text.Json;
using Orc.Cards;
using Orc.Game.Cards.Loading;

namespace Orc.Game.Cards.Data;

/// <summary>单卡载入失败（隔离留痕）。</summary>
/// <param name="File">文件路径。</param>
/// <param name="Error">失败原因。</param>
public sealed record CardDataLoadFailure(string File, string Error);

/// <summary>
/// 卡牌数据体载入结果（S8；Q14/Q15/Q17）：**纯数据产出**——注册由宿主完成
/// （定义集传给 <c>Match</c>；预制体经 <c>PrefabManager.RegisterPrefab</c>；效果声明经 <c>CardEffectRegistry.DeclarePrefab</c>）。
/// </summary>
/// <param name="Definitions">卡定义集（成功载入的卡）。</param>
/// <param name="Prefabs">内联效果预制体（含 id——载入期注册用；显式 id 缺失＝该条隔离）。</param>
/// <param name="EffectDeclarations">卡 → 效果预制体 id 清单（引用 ＋ 内联并入；注册用）。</param>
/// <param name="Failures">失败/隔离留痕（坏文件、坏单卡）。</param>
/// <param name="Warnings">软隔离留痕（未知组件、组件反序列化失败等）。</param>
public sealed record CardDataLoadResult(
    IReadOnlyList<CardDefinitionEntry> Definitions,
    IReadOnlyList<EffectSnapshot> Prefabs,
    IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> EffectDeclarations,
    IReadOnlyList<CardDataLoadFailure> Failures,
    IReadOnlyList<string> Warnings);

/// <summary>
/// **宽松解析结果**（R5/D40；<see cref="CardDataLoader.ParseLenient"/>）：字段级问题**不抛**，
/// 逐条记入 <see cref="Issues"/>；只有「真无法处理」的原文（JSON 语法／根节点形态／<c>schemaVersion</c> 解码契约）
/// 才落到 <see cref="Error"/>。
/// </summary>
/// <param name="Id">数据体声明的卡 id（缺失＝null——身份可由调用方按 sidecar 声明补位）。</param>
/// <param name="Definition">字段已按类型默认值补全的定义（含非法项＝null——D40「非法不生成」）。</param>
/// <param name="Issues">字段级问题清单（路径＋期望类型＋默认值＋原因；判定与套件同源）。</param>
/// <param name="Error">不可解析原因（可解析＝null）。</param>
public sealed record CardDataLenientParse(
    string? Id,
    CardDefinition? Definition,
    IReadOnlyList<CardFieldIssue> Issues,
    string? Error)
{
    /// <summary>原文是否可解析（字段问题不影响本值）。</summary>
    public bool IsParsed => Error is null;

    /// <summary>是否含非法项（卡级聚合：任一非法项 ⇒ 整卡不生成）。</summary>
    public bool HasInvalidIssue => Issues.Any(issue => issue.IsInvalid);
}

/// <summary>
/// **单文件宽松载入结果**（R5/D40；<see cref="CardDataLoader.LoadFileLenient"/>）：
/// 「解析产出 ＋ 字段级问题 ＋ 该文件的内联预制体/效果声明」；只有读文件失败与不可解析（JSON 语法等）才落 <see cref="Failure"/>。
/// </summary>
/// <param name="Id">数据体声明的卡 id（缺失＝null）。</param>
/// <param name="Definition">补全后的卡定义（含非法项＝null）。</param>
/// <param name="Issues">字段级问题清单。</param>
/// <param name="Prefabs">该文件的内联效果预制体（卡不生成时为空）。</param>
/// <param name="EffectDeclarations">该文件的效果声明（卡不生成时为空）。</param>
/// <param name="Failure">不可处理原因（可处理＝null）。</param>
public sealed record CardDataFileLoad(
    string? Id,
    CardDefinition? Definition,
    IReadOnlyList<CardFieldIssue> Issues,
    IReadOnlyList<EffectSnapshot> Prefabs,
    IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> EffectDeclarations,
    string? Failure)
{
    /// <summary>文件是否可处理（字段问题不影响本值）。</summary>
    public bool IsLoaded => Failure is null;
}

/// <summary>
/// 卡牌数据体载入器（S8）：扫描目录下的单卡数据体文件（<c>*.card.json</c>，Q14）、逐文件解析
/// （**单文件失败隔离**、不阻断整批——对齐 <c>PrefabManager.LoadDirectory</c> 口径），
/// 产出"卡定义集 ＋ 预制体注册 ＋ 效果声明"三样数据（assembly 不动注册面——由宿主接线）。
/// <para>
/// 两个入口并存、语义分离（R5/D40）：
/// <list type="bullet">
///   <item><see cref="LoadDirectory"/>／<see cref="LoadFiles"/>＝**严格入口**（数据体必填字段缺失即隔离该卡）。
///     既有调用方与行为不变。</item>
///   <item><see cref="ParseLenient"/>／<see cref="LoadFileLenient"/>＝**宽松入口**（注册面使用）：
///     逐字段解析，缺失/类型不符记入字段级问题清单（补类型默认值），只有 JSON 语法等「真无法处理」才判失败。
///     判定复用 <see cref="CardValiditySuite"/> 的 reason 枚举与 field 形状（不另造一套），并按 D40 分档：
///     任一非法项 ⇒ <see cref="CardDataLenientParse.Definition"/> 为 null（非法不生成）。</item>
/// </list>
/// </para>
/// </summary>
public static class CardDataLoader
{
    /// <summary>数据体文件模式（单卡一文件）。</summary>
    public const string FilePattern = "*.card.json";

    /// <summary>宽松读取的 JSON 选项（与 <see cref="CardDataJson"/> 读宽口径一致：容忍注释与尾逗号）。</summary>
    private static readonly JsonDocumentOptions LenientDocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>载入目录（不存在＝空结果）。</summary>
    /// <exception cref="ArgumentNullException">directory 为 null。</exception>
    public static CardDataLoadResult LoadDirectory(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory))
        {
            return new CardDataLoadResult(
                Array.Empty<CardDefinitionEntry>(),
                Array.Empty<EffectSnapshot>(),
                Array.Empty<(string, IReadOnlyList<string>)>(),
                Array.Empty<CardDataLoadFailure>(),
                Array.Empty<string>());
        }

        return LoadFiles(Directory.EnumerateFiles(directory, FilePattern, SearchOption.TopDirectoryOnly));
    }

    /// <summary>载入指定文件集（逐文件隔离；**严格入口**——必填字段缺失即隔离该卡）。</summary>
    /// <exception cref="ArgumentNullException">files 为 null。</exception>
    public static CardDataLoadResult LoadFiles(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var definitions = new List<CardDefinitionEntry>();
        var prefabs = new List<EffectSnapshot>();
        var declarations = new List<(string CardId, IReadOnlyList<string> PrefabIds)>();
        var failures = new List<CardDataLoadFailure>();
        var warnings = new List<string>();
        var cardIds = new HashSet<string>(StringComparer.Ordinal);
        var prefabIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new CardDataLoadFailure(file, $"读取失败：{ex.Message}"));
                continue;
            }

            if (!CardDataJson.TryDeserialize(json, out var body, out var fileWarnings, out var error))
            {
                failures.Add(new CardDataLoadFailure(file, error ?? "载入失败。"));
                continue;
            }

            foreach (var warning in fileWarnings)
            {
                warnings.Add($"{Path.GetFileName(file)}：{warning}");
            }

            if (!cardIds.Add(body.Id))
            {
                failures.Add(new CardDataLoadFailure(file, $"卡 id '{body.Id}' 重复（跳过——卡库键唯一）。"));
                continue;
            }

            CardDefinition definition;
            try
            {
                definition = new CardDefinition(body.Name, body.Components);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                failures.Add(new CardDataLoadFailure(file, $"卡定义非法：{ex.Message}"));
                continue;
            }

            definitions.Add(new CardDefinitionEntry(body.Id, definition));

            // 效果声明：引用 id ＋ 内联预制体（内联先注册——以 id 并入声明，装载链因此只有"引用"一条）。
            var ids = new List<string>();
            CollectEffects(body.Id, body.Components, prefabIds, prefabs, warnings, ids);
            if (ids.Count > 0)
            {
                declarations.Add((body.Id, ids));
            }
        }

        return new CardDataLoadResult(definitions, prefabs, declarations, failures, warnings);
    }

    /// <summary>
    /// **宽松解析**（R5/D40；注册面入口）：逐字段解析，字段缺失/类型不符⇒字段级问题（补类型默认值），**不抛**；
    /// JSON 语法错误／根节点非对象／<c>schemaVersion</c> 不符＝不可解析（<see cref="CardDataLenientParse.Error"/>）。
    /// </summary>
    /// <param name="json">数据体原文。</param>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static CardDataLenientParse ParseLenient(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        string? id;
        try
        {
            using var document = JsonDocument.Parse(json, LenientDocOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unparsable("卡牌数据体根节点须为对象。");
            }

            if (!TryGetProperty(root, "schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion))
            {
                return Unparsable("卡牌数据体缺少 schemaVersion（必填、整数）。");
            }

            if (schemaVersion != CardDataJson.SchemaVersion)
            {
                return Unparsable(
                    $"卡牌数据体 schema 版本不符：期望 {CardDataJson.SchemaVersion}，实际 {schemaVersion}（不静默降级）。");
            }

            id = GetString(root, "id");

            // 重复组件＝数据体结构性错误（同一组件每卡至多一份）：取哪一份都可能是错的那份 ⇒ 不猜、判不可解析
            // （与 <see cref="CardDataJson"/> 的拒绝口径一致，避免宽松入口把「哪份生效」静默定死）。
            if (TryGetProperty(root, "components", out var componentsElement)
                && componentsElement.ValueKind == JsonValueKind.Array)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in componentsElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var componentName = GetString(element, "component");
                    if (!string.IsNullOrWhiteSpace(componentName) && !seen.Add(componentName))
                    {
                        return Unparsable(
                            $"卡牌数据体含重复组件 '{componentName}'（同一组件每卡至多一份——拒绝）。");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            return Unparsable($"卡牌数据体 JSON 非法：{ex.Message}");
        }

        // 字段级判定＝合法性检测套件（D40 单一真源）：只取数据体面（id／name／components.*），
        // 展示面（卡图／描述）与注册面元数据（source／version／sidecar）由各自的判定面负责。
        // 注册面此刻还没有应用层 ID（身份要等 sidecar 匹配之后），套件的数据体判定也不使用它。
        var evaluation = CardValiditySuite.BuiltIn.Evaluate(new CardValidityInput(string.Empty) { RawJson = json });
        var issues = evaluation.Issues
            .Where(issue => IsDataBodyPath(issue.Path))
            .ToArray();

        var definition = issues.Any(issue => issue.IsInvalid) ? null : evaluation.Definition;
        return new CardDataLenientParse(
            string.IsNullOrWhiteSpace(id) ? null : id,
            definition,
            issues,
            Error: null);
    }

    /// <summary>
    /// **单文件宽松载入**（R5/D40）：读文件 ＋ <see cref="ParseLenient"/> ＋ 该文件的内联预制体/效果声明。
    /// 读失败与不可解析＝<see cref="CardDataFileLoad.Failure"/>；字段级问题只进 <see cref="CardDataFileLoad.Issues"/>。
    /// </summary>
    /// <param name="file">数据体文件绝对路径。</param>
    /// <exception cref="ArgumentException">file 为 null/空白。</exception>
    public static CardDataFileLoad LoadFileLenient(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"读取失败：{ex.Message}");
        }

        var parsed = ParseLenient(json);
        if (parsed.Error is not null)
        {
            return Failed(parsed.Error);
        }

        if (parsed.Definition is null || parsed.Id is null)
        {
            // 非法卡不生成（D40）：其内联预制体/效果声明一并不产出（与既有「坏卡整文件隔离」口径一致）。
            return new CardDataFileLoad(
                parsed.Id,
                Definition: null,
                parsed.Issues,
                Array.Empty<EffectSnapshot>(),
                Array.Empty<(string, IReadOnlyList<string>)>(),
                Failure: null);
        }

        var prefabs = new List<EffectSnapshot>();
        var declarations = new List<(string CardId, IReadOnlyList<string> PrefabIds)>();
        var ids = new List<string>();
        CollectEffects(
            parsed.Id,
            parsed.Definition.Components,
            new HashSet<string>(StringComparer.Ordinal),
            prefabs,
            new List<string>(),
            ids);
        if (ids.Count > 0)
        {
            declarations.Add((parsed.Id, ids));
        }

        return new CardDataFileLoad(parsed.Id, parsed.Definition, parsed.Issues, prefabs, declarations, Failure: null);

        static CardDataFileLoad Failed(string failure)
            => new(null, null, Array.Empty<CardFieldIssue>(), Array.Empty<EffectSnapshot>(),
                Array.Empty<(string, IReadOnlyList<string>)>(), failure);
    }

    /// <summary>
    /// 收集效果声明（引用 id ＋ 内联预制体）：内联先注册——以 id 并入声明，装载链因此只有「引用」一条。
    /// 重复 id／内联缺 <c>root.id</c>＝隔离该条并留痕（不阻断其余）。
    /// </summary>
    private static void CollectEffects(
        string cardId,
        IReadOnlyList<ICardDataComponentDefinition> components,
        HashSet<string> prefabIds,
        List<EffectSnapshot> prefabs,
        List<string> warnings,
        List<string> ids)
    {
        var effects = components.OfType<Components.EffectsDefinition>().FirstOrDefault();
        if (effects is null)
        {
            return;
        }

        foreach (var prefabId in effects.Prefabs)
        {
            if (!prefabIds.Add(prefabId))
            {
                warnings.Add($"{cardId}：效果预制体 id '{prefabId}' 重复注册（隔离该条）。");
                continue;
            }

            ids.Add(prefabId);
        }

        foreach (var snapshot in effects.Inline)
        {
            var inlineId = snapshot.Root.Id;
            if (string.IsNullOrWhiteSpace(inlineId))
            {
                warnings.Add($"{cardId}：内联预制体缺少 root.id（隔离该条——请显式给出 id）。");
                continue;
            }

            if (!prefabIds.Add(inlineId))
            {
                warnings.Add($"{cardId}：内联预制体 id '{inlineId}' 与已注册项冲突（隔离该条）。");
                continue;
            }

            prefabs.Add(snapshot);
            ids.Add(inlineId);
        }
    }

    /// <summary>数据体面路径（套件里由数据体原文判定出的字段：<c>id</c>／<c>name</c>／<c>components.*</c>）。</summary>
    private static bool IsDataBodyPath(string path)
        => path is "id" or "name" || path.StartsWith("components", StringComparison.Ordinal);

    private static CardDataLenientParse Unparsable(string error)
        => new(null, null, Array.Empty<CardFieldIssue>(), error);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
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
