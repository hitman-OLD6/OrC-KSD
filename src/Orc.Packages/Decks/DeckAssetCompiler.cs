using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Game;

namespace Orc.Packages.Decks;

/// <summary>卡组编译结果：可用的卡组对象 ＋ 内容违规 ＋ 不可处理原因（三者至多一项非空）。</summary>
/// <param name="Definition">合法卡组（非法／不可处理＝null——D6「非法不生成」）。</param>
/// <param name="Violations">内容违规（<c>DeckViolation</c>，**列全**）。</param>
/// <param name="Failure">不可处理原因（JSON 不可解析／身份不符；与内容违规互斥）。</param>
public sealed record DeckCompileResult(
    DeckDefinition? Definition,
    IReadOnlyList<DeckViolation> Violations,
    string? Failure)
{
    /// <summary>
    /// 不可处理的原因码（注册面照此选诊断类别）：默认 <see cref="AssetDiagnosticCode.FailedFile"/>；
    /// 身份不符时为 <see cref="AssetDiagnosticCode.IdMismatch"/>。
    /// </summary>
    public AssetDiagnosticCode FailureCode { get; init; } = AssetDiagnosticCode.FailedFile;

    /// <summary>是否产出合法卡组。</summary>
    public bool IsValid => Definition is not null;
}

/// <summary>
/// **卡组资产的判定编排**（R3 第二段／R5／R7／D44）：解析数据体 → 归一卡引用 → 定稿构筑配置（含 R12 推导）
/// → 跑 <see cref="DeckRules"/> 的判定。注册期与编辑器提交期**共用本方法**——这是「一份实现三处消费」的
/// 那个「一份」，调用方只负责把违规装进自己的载体。
/// <para>
/// 资产通路**恒 strict**（D10）：卡组是「作品是否合格」，不存在放宽档；要跑变态卡组请走手写卡列表通路。
/// </para>
/// </summary>
public static class DeckAssetCompiler
{
    /// <summary>编译一份卡组数据体。</summary>
    /// <param name="json">数据体原文。</param>
    /// <param name="expectedId">sidecar 声明的局部 ID（与数据体 <c>id</c> 不一致＝不可处理）。</param>
    /// <param name="packageId">所属包 id（裸局部 ID 的前缀来源）。</param>
    /// <param name="pool">卡池（注册集合内通过卡牌判定的卡；D13）。</param>
    /// <param name="requiredSize">要求张数（D11：由调用方提供，**不写死**；&lt;=0＝不校验张数）。</param>
    public static DeckCompileResult Compile(
        string json,
        string expectedId,
        string packageId,
        IReadOnlyDictionary<string, CardDefinition> pool,
        int requiredSize)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(pool);

        var parse = DeckDataJson.Parse(json);
        if (!parse.IsParsed)
        {
            return Failed(parse.Failure!);
        }

        var body = parse.Body!;
        if (!string.Equals(body.Id, expectedId, StringComparison.Ordinal))
        {
            return Failed(
                $"卡组数据体 id '{body.Id ?? "<缺失>"}' 与 sidecar 声明的 '{expectedId}' 不一致（该资产非法，不生成）。",
                AssetDiagnosticCode.IdMismatch);
        }

        var violations = new List<DeckViolation>(parse.Violations);

        // ① 卡组自身组：展示名
        if (DeckRules.ValidateName(body.Name) is { } nameViolation)
        {
            violations.Add(nameViolation);
        }

        // ② 卡引用归一（D48：裸局部 ID 只是输入便利，定义层一律全名）
        var entries = new List<DeckEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in body.Cards)
        {
            var appId = AssetAppId.IsAppId(card.Id) ? card.Id : AssetAppId.Compose(packageId, card.Id);
            if (!seen.Add(appId))
            {
                // 裸局部 ID 与应用层 ID 混写指向同一张卡，归一后即重复——聚合形式下重复＝歧义。
                violations.Add(new DeckViolation(
                    "duplicate-entry", appId, $"卡组重复声明 '{appId}'（同一张卡在 cards 中出现多次即重复）。"));
                continue;
            }

            entries.Add(new DeckEntry(appId, card.Count));
        }

        // ③ 构筑配置：主国必填、显式盟国须合法（D16/D17）
        if (DeckRules.ValidateFaction(new PlayerDeckConfiguration(body.MainFaction, body.AllyFaction)) is { } factionViolation)
        {
            violations.Add(factionViolation);
        }

        if (body.MainFaction is not { Length: > 0 }
            || !FactionRules.IsMajorFaction(body.MainFaction)
            || (body.AllyFaction is { Length: > 0 } ally && !FactionRules.IsValidAlly(body.MainFaction, ally)))
        {
            // 主国/盟国本身不成立 ⇒ 归属与配额无从判，且推导不成立：到此为止（违规已列全）。
            return new DeckCompileResult(null, violations, Failure: null);
        }

        // ④ 盟国定稿：显式优先，否则按 R12 推导（推导先于判定，结果落到定义上不再重算）
        var allyDerived = body.AllyFaction is not { Length: > 0 };
        var allyFaction = allyDerived
            ? AllyFactionDerivation.Derive(body.MainFaction, entries, pool)
            : body.AllyFaction!;
        var deckConfig = new PlayerDeckConfiguration(body.MainFaction, allyFaction);

        var definition = new DeckDefinition(body.Id!, body.Name ?? string.Empty, deckConfig, allyDerived, entries);
        var cardIds = definition.ToCardIds();

        // ⑤ 逐卡检查（size 在 Validate 内部先于逐卡——D66 的顺序由此保证）
        violations.AddRange(DeckRules.Validate(cardIds, pool, requiredSize, DeckRuleTier.Strict));
        violations.AddRange(DeckRules.ValidateFactionComposition(cardIds, pool, deckConfig, DeckRuleTier.Strict));

        return violations.Count == 0
            ? new DeckCompileResult(definition, violations, Failure: null)
            : new DeckCompileResult(null, violations, Failure: null);
    }

    private static DeckCompileResult Failed(string failure, AssetDiagnosticCode code = AssetDiagnosticCode.FailedFile)
        => new(null, Array.Empty<DeckViolation>(), failure) { FailureCode = code };
}
