using Orc.Game.Cards.Data;

namespace Orc.Game.Cards.Loading;

/// <summary>
/// 一次卡级判定（D40）的**输入事实**：注册面已确定的元数据事实 ＋ 源文件原文 ＋ 外部资源解析结果。
/// <para>
/// 只装载「判定所需的最小面」：套件不做任何解析/IO 之外的推断，也不感知资产 GUID（那是注册面/报告面的事）。
/// 源文件原文缺失（<see cref="RawJson"/> 为 null）时退化为只做元数据级判定，并把注册面已生成的
/// <see cref="RegisteredDefinition"/> 作为产出（不重复生成）。
/// </para>
/// </summary>
public sealed record CardValidityInput(string AppId)
{
    /// <summary>sidecar 声明的包内局部 ID（null＝无 sidecar 声明）。用于 R2/D48 的 <c>id</c> 一致性判定。</summary>
    public string? MetadataLocalId { get; init; }

    /// <summary>是否有 sidecar（false＝按「无元数据」判定，记一条 <see cref="CardIssueReason.SidecarMissing"/> 警报）。</summary>
    public bool HasSidecar { get; init; } = true;

    /// <summary>sidecar 声明的**非法版本原文**（非 null ⇒ 该资产非法、不生成；R17/D27）。</summary>
    public string? InvalidVersion { get; init; }

    /// <summary>源文件绝对路径（null＝无源文件，可判定性下降；指向不存在文件 ⇒ 非法）。</summary>
    public string? SourcePath { get; init; }

    /// <summary>卡数据体原文（null＝无可读原文 ⇒ 只做元数据级判定）。</summary>
    public string? RawJson { get; init; }

    /// <summary>注册面已生成的卡定义（原文缺失时的产出回退；也为 null ⇒ 视为源文件缺失＝非法）。</summary>
    public CardDefinition? RegisteredDefinition { get; init; }

    /// <summary>
    /// 注册期已记录的**字段级问题**（R5/D40；来自注册面宽松解析，如
    /// <c>AssetRegistration.LoadIssues</c>）。判定末并入：与套件自身判定按「路径 ＋ 原因」去重，
    /// 保证同一事实只计一条；原文缺失时它是字段问题的唯一来源。
    /// </summary>
    public IReadOnlyList<CardFieldIssue> RegistrationIssues { get; init; } = Array.Empty<CardFieldIssue>();

    /// <summary>卡图路径（引擎无美术概念——由宿主/前端解析后交出；null/空白＝缺失警报）。</summary>
    public string? CardImagePath { get; init; }

    /// <summary>卡图文件存在性判定（null＝用 <see cref="File.Exists(string)"/>；宿主可注入包根相对解析）。</summary>
    public Func<string, bool>? ImageFileExists { get; init; }

    /// <summary>描述文本（来自卡面文本表或数据体 <c>description</c>；null/空白＝缺失警报）。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// 效果引用存在性判定（参数＝数据体里写的引用串，裸局部 ID 或应用层 ID）。
    /// null＝**不判定**引用完整性（无可信来源时不得臆断非法）。
    /// </summary>
    public Func<string, bool>? EffectReferenceExists { get; init; }
}

/// <summary>
/// 判定上下文（供插件**追加**检查项）：暴露套件已解析出的数据体事实，插件不得改三档骨架（D35）。
/// </summary>
public sealed class CardValidityContext
{
    internal CardValidityContext(
        CardValidityInput input,
        string? localId,
        string? name,
        string? category,
        IReadOnlyList<ICardDataComponentDefinition> components,
        IReadOnlyList<string> effectReferences)
    {
        Input = input;
        LocalId = localId;
        Name = name;
        Category = category;
        Components = components;
        EffectReferences = effectReferences;
    }

    /// <summary>原始输入。</summary>
    public CardValidityInput Input { get; }

    /// <summary>数据体声明的卡 id（缺失＝null）。</summary>
    public string? LocalId { get; }

    /// <summary>数据体声明的名称（缺失＝null）。</summary>
    public string? Name { get; }

    /// <summary>解析出的类别（<c>unit</c>／<c>command</c>／<c>counter</c>；类别缺失＝null）。</summary>
    public string? Category { get; }

    /// <summary>已解析的内置组件定义（不含被补全的默认组件）。</summary>
    public IReadOnlyList<ICardDataComponentDefinition> Components { get; }

    /// <summary>数据体声明的效果预制体引用（原文）。</summary>
    public IReadOnlyList<string> EffectReferences { get; }

    /// <summary>数据体是否声明了某内置组件（大小写不敏感的组件名）。</summary>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    public bool HasComponent(string name)
        => Components.Any(component => string.Equals(component.ComponentName, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 插件**追加**检查项（D35：引擎内置骨架固定，插件只能追加、不能改三档骨架与聚合规则）。
/// </summary>
public interface ICardValidityCheck
{
    /// <summary>检查项名称（诊断/审计用，非空）。</summary>
    string Name { get; }

    /// <summary>追加判定（返回的问题按同一聚合规则并入：任一非法 ⇒ 整卡非法）。</summary>
    IEnumerable<CardFieldIssue> Evaluate(CardValidityContext context);
}

/// <summary>一次卡级判定的产出：档位 ＋ 问题清单 ＋ 生成物（非法＝null——**非法不生成**）。</summary>
/// <param name="Tier">卡级聚合档位（任一非法项 ⇒ 非法）。</param>
/// <param name="Issues">问题清单（判定序：元数据 → 数据体字段 → 资源 → 插件追加）。</param>
/// <param name="Definition">生成的卡定义（非法＝null；警报＝已补类型默认值）。</param>
public sealed record CardValidityResult(
    CardLoadTier Tier,
    IReadOnlyList<CardFieldIssue> Issues,
    CardDefinition? Definition)
{
    /// <summary>是否产出可装载的卡（合法或警报）。</summary>
    public bool IsPlayable => Tier != CardLoadTier.Invalid && Definition is not null;

    /// <summary>问题涉及的字段路径（去重、保判定序）。</summary>
    public IReadOnlyList<string> FieldPaths
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return Issues.Where(issue => seen.Add(issue.Path)).Select(issue => issue.Path).ToArray();
        }
    }

    /// <summary>非法项（卡级聚合依据）。</summary>
    public IReadOnlyList<CardFieldIssue> InvalidIssues => Issues.Where(issue => issue.IsInvalid).ToArray();
}
