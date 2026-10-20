using Orc.Cards;
using Orc.Game.Cards;

namespace Orc.Packages;

/// <summary>
/// 卡包词条声明（<c>keywords.json</c> 条目；沿前端 <c>PackKeywordDecl</c> 形状）：
/// 标识 ＋ 显示名 ＋ 是否对战词条（打标）＋ 效果绑定清单。
/// </summary>
/// <param name="Id">词条标识（引擎 id）。</param>
/// <param name="Label">显示名。</param>
/// <param name="IsBattleKeyword">是否对战词条（打标）。</param>
/// <param name="Bindings">词条 → 效果预制体的绑定声明。</param>
/// <param name="Program">
/// 可选的词条程序（<see cref="KeywordProgram"/> JSON 原文；空＝纯标识词条）。
/// 有程序时该词条注册为 <see cref="ProgramKeywordComponent"/>（规则/技能承载），允许零效果绑定。
/// </param>
public sealed record CardPackKeyword(
    string Id,
    string Label,
    bool IsBattleKeyword,
    IReadOnlyList<KeywordEffectBinding> Bindings,
    string? Program = null);

/// <summary>
/// 已装载卡包的**内容**（纯数据产出——注册由调用方按装配序完成）。
/// 卡 id 已按 <c>pack/&lt;包id&gt;/&lt;原卡id&gt;</c> 前缀化（前缀约定为 UI 冻结契约，不得更改）。
/// </summary>
public sealed class CardPackContent
{
    internal CardPackContent(
        PackageManifest manifest,
        string rootDirectory,
        IReadOnlyList<CardDefinitionEntry> definitions,
        IReadOnlyList<EffectSnapshot> inlinePrefabs,
        IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> declarations,
        string? effectsDirectory,
        IReadOnlyList<CardPackKeyword> keywords,
        IReadOnlyDictionary<string, string> texts)
    {
        Manifest = manifest;
        RootDirectory = rootDirectory;
        Definitions = definitions;
        InlinePrefabs = inlinePrefabs;
        Declarations = declarations;
        EffectsDirectory = effectsDirectory;
        Keywords = keywords;
        Texts = texts;
    }

    /// <summary>
    /// 卡面文本表（**包内原卡 id** → 文本；`texts.json` 原文）。
    /// 供消费方构造 UI 定义（图鉴/构筑）时补齐卡面文本——规则文本属界面数据，引擎不参与渲染。
    /// </summary>
    public IReadOnlyDictionary<string, string> Texts { get; }

    /// <summary>包清单。</summary>
    public PackageManifest Manifest { get; }

    /// <summary>包根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>卡定义集（id 已前缀化）。</summary>
    public IReadOnlyList<CardDefinitionEntry> Definitions { get; }

    /// <summary>内联效果预制体（含文本转换产出的效果）。</summary>
    public IReadOnlyList<EffectSnapshot> InlinePrefabs { get; }

    /// <summary>卡 → 效果预制体 id 声明（cardId 已前缀化，与定义集同源）。</summary>
    public IReadOnlyList<(string CardId, IReadOnlyList<string> PrefabIds)> Declarations { get; }

    /// <summary>效果库目录（不存在＝null）。</summary>
    public string? EffectsDirectory { get; }

    /// <summary>词条声明（<c>keywords.json</c>；不存在＝空）。</summary>
    public IReadOnlyList<CardPackKeyword> Keywords { get; }

    /// <summary>卡 id 前缀（<c>pack/&lt;包id&gt;/</c>）。</summary>
    public string Prefix => $"pack/{Manifest.Id}/";

    /// <summary>把包内原卡 id 前缀化为全局唯一 id。</summary>
    /// <exception cref="ArgumentNullException">cardId 为 null。</exception>
    public string Prefixed(string cardId)
    {
        ArgumentNullException.ThrowIfNull(cardId);
        return Prefix + cardId;
    }
}
