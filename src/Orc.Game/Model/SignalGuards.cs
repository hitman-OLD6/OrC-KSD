using Orc.Core.Model;

namespace Orc.Game.Model;

/// <summary>一条守卫声明（用于 <see cref="SignalGuards.TryDeriveRequiresHost"/> 的交集推导）。</summary>
/// <param name="Member">成员名（错误文案要用它指出「哪个成员在哪个宿主类型上永不命中」）。</param>
/// <param name="Signal">该成员所挂的信号名（推导依据）。</param>
/// <param name="Guard">守卫档。</param>
public sealed record GuardedMember(string Member, string Signal, HandlerGuard Guard);

/// <summary>
/// **守卫 ↔ 宿主**的推导与白名单校验（R32／D24／D27）。
/// <para>
/// 分层职责：两张白名单（<c>Subjects</c>／<c>Players</c>）各自跟在信号定义处（<see cref="KernelSignals"/> 与
/// <see cref="GameSignals"/>，D26 的同源理由——<c>Orc.Game → Orc.Core</c> 单向引用，合并表只能落在本层），
/// 本类只做**合并与推导**，不持有数据。
/// </para>
/// <para>
/// **`HostKind` 没有独立 `Card` 值**（<c>Any</c> ＝ Unit|Order|Counter|Hq），所以 <c>card.*</c> ＋ <c>self</c>
/// 不收紧宿主，只有 <c>unit.*</c>／<c>counter.*</c> 才收紧——这正是 D24 的推导表。
/// </para>
/// </summary>
public static class SignalGuards
{
    /// <summary>全部信号名（内核 4 ＋ 游戏层 35，各自声明序；模板库的确定性基础）。</summary>
    public static IReadOnlyList<string> Names { get; } = KernelSignals.Names.Concat(GameSignals.Names).ToArray();

    /// <summary>合并后的信号中文名（内核 4 ＋ 游戏层 35；模板 <c>label</c> 的派生源）。</summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } = Merge(KernelSignals.Labels, GameSignals.Labels);

    /// <summary>合并后的语义边界说明（模板 <c>description</c> 的派生源，D25）。</summary>
    public static IReadOnlyDictionary<string, string> Notes { get; } = Merge(KernelSignals.Notes, GameSignals.Notes);

    /// <summary>合并后的主体卡字段白名单（未登记＝该信号无 <c>self</c> 档）。</summary>
    public static IReadOnlyDictionary<string, string> Subjects { get; } = Merge(KernelSignals.Subjects, GameSignals.Subjects);

    /// <summary>合并后的玩家字段白名单（未登记＝该信号无 <c>owner-*</c> 档）。</summary>
    public static IReadOnlyDictionary<string, string> Players { get; } = Merge(KernelSignals.Players, GameSignals.Players);

    private static readonly IReadOnlyDictionary<string, MemberDefinition> MembersByName = BuildMemberIndex();

    /// <summary>信号成员声明（非信号名＝null）。</summary>
    public static MemberDefinition? MemberOf(string signal)
        => MembersByName.TryGetValue(signal, out var member) ? member : null;

    /// <summary>该信号是否有 <c>self</c> 档（**只由白名单决定**，不按载荷顺序或类型推断）。</summary>
    public static bool HasSelf(string signal) => Subjects.ContainsKey(signal);

    /// <summary>该信号是否有 <c>owner-*</c> 档（同上）。</summary>
    public static bool HasOwner(string signal) => Players.ContainsKey(signal);

    /// <summary>
    /// 信号 ＋ 守卫档 → 代码合成参数（字段名来自白名单）。
    /// </summary>
    /// <param name="guard">守卫档。</param>
    /// <param name="signal">信号名。</param>
    /// <param name="spec">成功时的合成参数（<see cref="HandlerGuard.Any"/>＝返回 false）。</param>
    /// <param name="error">失败原因（白名单未登记／字段不在载荷里）。</param>
    public static bool TryResolve(HandlerGuard guard, string signal, out HandlerGuardSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (guard == HandlerGuard.Any)
        {
            return false;
        }

        if (MemberOf(signal) is null)
        {
            error = $"信号 '{signal}' 未在信号表里登记（无法为守卫档 '{HandlerGuardNames.Format(guard)}' 解析比较对象）。";
            return false;
        }

        var needsSubject = guard == HandlerGuard.Self;
        var table = needsSubject ? Subjects : Players;
        if (!table.TryGetValue(signal, out var field))
        {
            error = needsSubject
                ? $"信号 '{signal}' 未在主体卡字段白名单（Subjects）登记：不出 self 档（R32／D27）。"
                : $"信号 '{signal}' 未在玩家字段白名单（Players）登记：不出 owner-* 档（R32／D27）。";
            return false;
        }

        // 专属守护（D27 第 ② 条）：白名单里的字段必须真实存在于该信号的载荷声明中——错字必须报错，
        // 不允许静默退化成「不出该档」。
        var member = MemberOf(signal)!;
        if (!member.View.Fields.Any(f => string.Equals(f.Name, field, StringComparison.Ordinal)))
        {
            error = $"白名单把信号 '{signal}' 的比较对象登记为字段 '{field}'，但该信号的载荷声明里没有这个字段"
                + $"（已声明：{string.Join("／", member.View.Fields.Select(static f => f.Name))}）——白名单写错了。";
            return false;
        }

        spec = new HandlerGuardSpec(
            guard,
            needsSubject ? field : string.Empty,
            needsSubject ? string.Empty : field);
        return true;
    }

    /// <summary>
    /// 守卫档 → 推导宿主集（D24 的位标志真值表）：
    /// <c>self</c> 档按信号前缀（<c>unit.*</c>→Unit、<c>counter.*</c>→Counter、其余→Any）；
    /// <c>owner-*</c>／<c>any</c> → Any（不收紧）。
    /// </summary>
    public static HostKind DeriveHostKind(HandlerGuard guard, string signal)
    {
        if (guard != HandlerGuard.Self)
        {
            return HostKind.Any;
        }

        if (signal.StartsWith("unit.", StringComparison.Ordinal))
        {
            return HostKind.Unit;
        }

        if (signal.StartsWith("counter.", StringComparison.Ordinal))
        {
            return HostKind.Counter;
        }

        return HostKind.Any;
    }

    /// <summary>
    /// 多抽象成员模板的 <c>requires-host</c> 推导：**交集**，并校验「声明的宿主不宽于交集」。
    /// <para>
    /// 约束：<c>declared ⊆ ⋂(各守卫成员的推导宿主集)</c>。声明更宽 ⇒ 拒绝（否则「某成员在声明宿主上永不命中」，
    /// 正是 R32 要消灭的静默失效）；交集为空 ⇒ 拒绝（如 <c>unit.*</c> ＋ <c>counter.*</c>：一个组件装不到两种互斥宿主上）。
    /// </para>
    /// </summary>
    /// <param name="members">守卫成员（成员名／信号名／档）。空＝无约束（返回 <see cref="HostKind.Any"/>）。</param>
    /// <param name="declared">资产声明的 <c>requires-host</c>。</param>
    /// <param name="host">推导出的宿主集（失败＝None）。</param>
    /// <param name="error">失败原因（含互斥的两个成员名）。</param>
    public static bool TryDeriveRequiresHost(
        IReadOnlyList<GuardedMember> members, HostKind declared, out HostKind host, out string? error)
    {
        host = HostKind.Any;
        error = null;

        GuardedMember? narrowedBy = null;
        foreach (var member in members)
        {
            var derived = DeriveHostKind(member.Guard, member.Signal);
            if (derived == HostKind.Any)
            {
                continue;
            }

            var next = host & derived;
            if (next == HostKind.None)
            {
                error = $"守卫组合不成立：成员 '{narrowedBy?.Member}'（{narrowedBy?.Signal} → "
                    + $"{(narrowedBy is { } p ? AssetSchema.FormatHostKind(DeriveHostKind(p.Guard, p.Signal)) : "?")}）"
                    + $"与成员 '{member.Member}'（{member.Signal} → {AssetSchema.FormatHostKind(derived)}）的宿主集**交集为空**："
                    + "一个组件无法同时装在两种互斥宿主上（R32 第 7 条）。";
                host = HostKind.None;
                return false;
            }

            host = next;
            narrowedBy ??= member;
        }

        if ((declared & host) != declared)
        {
            // 声明比交集更宽：指出「哪个成员在哪个宿主类型上永不命中」——
            // 判据是「该成员的推导宿主集与 neverHits 无交集」（即它在这些宿主上一个都命中不了）。
            var intersection = host;
            var neverHits = declared & ~host;
            var starving = members
                .Where(m => (DeriveHostKind(m.Guard, m.Signal) & neverHits) == 0)
                .Select(m => $"成员 '{m.Member}'（{m.Signal} → "
                    + $"{AssetSchema.FormatHostKind(DeriveHostKind(m.Guard, m.Signal))}）")
                .ToArray();

            error = $"声明的 requires-host={AssetSchema.FormatHostKind(declared)} 宽于各守卫成员推导出的交集 "
                + $"{AssetSchema.FormatHostKind(intersection)}："
                + (starving.Length == 0
                    ? string.Empty
                    : $"{string.Join("、", starving)} 在 {AssetSchema.FormatHostKind(neverHits)} 上**永不命中**")
                + $"（R32 第 6 条；交集＝{AssetSchema.FormatHostKind(intersection)}）。";
            host = HostKind.None;
            return false;
        }

        return true;
    }

    /// <summary>
    /// **白名单专属守护**（D27 的第 ① ② 两条，逐层各查一遍）：表里的信号必须存在于**该层**的 <c>Names</c>，
    /// 字段名必须真实存在于该信号的载荷声明中。返回全部错误（空＝通过）。
    /// <para>逐层而不是合并后查：合并会让「游戏层误登记内核信号」溜过去，也就查不出跨层错字。</para>
    /// </summary>
    public static IReadOnlyList<string> ValidateWhitelists()
    {
        var errors = new List<string>();
        ValidateLayer("内核", KernelSignals.Names, KernelSignals.Members, errors);
        ValidateLayer("游戏层", GameSignals.Names, GameSignals.Members, errors);
        return errors;

        void ValidateLayer(
            string layer, IReadOnlyList<string> names, IReadOnlyList<MemberDefinition> members, List<string> sink)
        {
            var subjects = layer == "内核" ? KernelSignals.Subjects : GameSignals.Subjects;
            var players = layer == "内核" ? KernelSignals.Players : GameSignals.Players;
            var labels = layer == "内核" ? KernelSignals.Labels : GameSignals.Labels;

            sink.AddRange(ValidateTable(layer, names, members, subjects, "Subjects"));
            sink.AddRange(ValidateTable(layer, names, members, players, "Players"));
            sink.AddRange(ValidateTable(layer, names, members, labels, "Labels"));
        }
    }

    /// <summary>
    /// 校验**一张**白名单表（逐层各查一遍的单元）：信号必须存在于该层 <c>names</c>，
    /// 字段名必须真实存在于该信号的载荷声明中；<c>Labels</c> 的值是中文名（只查非空）。
    /// <para>公开只为了让「错字必须报错」这条可被直接测试（不靠改全局表来构造坏数据）。</para>
    /// </summary>
    public static IReadOnlyList<string> ValidateTable(
        string layer,
        IReadOnlyList<string> names,
        IReadOnlyList<MemberDefinition> members,
        IReadOnlyDictionary<string, string> table,
        string tableName)
    {
        var errors = new List<string>();
        var nameSet = names.ToHashSet(StringComparer.Ordinal);
        var byName = members.ToDictionary(static m => m.Name, StringComparer.Ordinal);

        foreach (var (signal, value) in table)
        {
            if (!nameSet.Contains(signal))
            {
                errors.Add($"{layer} {tableName} 里的 '{signal}' 不在该层的信号表里（跨层登记或拼写错误）。");
                continue;
            }

            if (tableName == "Labels")
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    errors.Add($"{layer} Labels['{signal}'] 为空：信号中文名必填。");
                }

                continue;
            }

            if (!byName[signal].View.Fields.Any(f => string.Equals(f.Name, value, StringComparison.Ordinal)))
            {
                errors.Add(
                    $"{layer} {tableName}['{signal}'] = '{value}'，但该信号的载荷声明里没有这个字段"
                    + $"（已声明：{string.Join("／", byName[signal].View.Fields.Select(static f => f.Name))}）。");
            }
        }

        return errors;
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var pair in second)
        {
            merged.TryAdd(pair.Key, pair.Value); // 同名＝不覆盖（跨层重名由 ValidateWhitelists 之外的集合守护兜住）
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, MemberDefinition> BuildMemberIndex()
    {
        var index = new Dictionary<string, MemberDefinition>(StringComparer.Ordinal);
        foreach (var member in KernelSignals.Members.Concat(GameSignals.Members))
        {
            index.TryAdd(member.Name, member);
        }

        return index;
    }
}
