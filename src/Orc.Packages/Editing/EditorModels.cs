using Orc.Core;

namespace Orc.Packages.Editing;

/// <summary>编辑会话结束方式（R10：转正／放弃）。</summary>
public enum EditorSessionEndMode
{
    /// <summary>转正（＝ <see cref="AssetEditor.Promote"/>）。</summary>
    Save = 0,

    /// <summary>放弃（删除整个临时目录，含测试 lock）。</summary>
    Discard = 1,
}

/// <summary>
/// `session.begin` 的入参（两种模式互斥）：编辑已有包 ＝ <see cref="Edit"/>；
/// 新建包 ＝ <see cref="CreateNew"/>（R8：**必须**给定期望依赖的卡包 ID）。
/// </summary>
public sealed record EditorSessionRequest
{
    private EditorSessionRequest()
    {
    }

    /// <summary>已有包根目录（编辑模式）。</summary>
    public string? PackageRoot { get; private init; }

    /// <summary>新包 id（新建模式；人类可读）。</summary>
    public string? PackageId { get; private init; }

    /// <summary>新包父目录（新建模式；`&lt;父目录&gt;/&lt;包id&gt;` 为最终正式包目录）。</summary>
    public string? ParentDirectory { get; private init; }

    /// <summary>新包显示名（可选）。</summary>
    public string? Name { get; private init; }

    /// <summary>期望依赖的卡包 ID（新建模式必填，至少一项）。</summary>
    public IReadOnlyList<string> DependsOnPackages { get; private init; } = Array.Empty<string>();

    /// <summary>是否新建包。</summary>
    public bool IsCreateNew => PackageId is not null;

    /// <summary>编辑已有卡包。</summary>
    /// <param name="packageRoot">包根目录（含 manifest.json）。</param>
    public static EditorSessionRequest Edit(string packageRoot)
        => new() { PackageRoot = packageRoot };

    /// <summary>新建卡包（R8：依赖包 ID 必填）。</summary>
    /// <param name="packageId">新包 id。</param>
    /// <param name="parentDirectory">父目录（最终包目录＝<c>&lt;父目录&gt;/&lt;包id&gt;</c>）。</param>
    /// <param name="dependsOnPackages">期望依赖的卡包 ID（至少一项）。</param>
    /// <param name="name">显示名（可选）。</param>
    public static EditorSessionRequest CreateNew(
        string packageId,
        string parentDirectory,
        IEnumerable<string> dependsOnPackages,
        string? name = null)
        => new()
        {
            PackageId = packageId,
            ParentDirectory = parentDirectory,
            DependsOnPackages = (dependsOnPackages ?? throw new ArgumentNullException(nameof(dependsOnPackages)))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Name = name,
        };
}

/// <summary>`pack.list` 条目：一个**可编辑卡包**（插件不在列表内；<c>*.tmp</c> 目录亦不列出）。</summary>
/// <param name="PackageId">包 id。</param>
/// <param name="Name">显示名（未声明＝null）。</param>
/// <param name="RootDirectory">包根绝对路径（归一）。</param>
/// <param name="Version">包版本（内容指纹；临时包带 <c>-tmp</c>）。</param>
/// <param name="HasTemporaryPackage">同级是否已存在 <c>&lt;包目录&gt;.tmp</c>（存在即不可开始编辑）。</param>
public sealed record EditablePackageInfo(
    string PackageId,
    string? Name,
    string RootDirectory,
    PackageVersion Version,
    bool HasTemporaryPackage);

/// <summary>`session.begin` 的产出（会话快照）。</summary>
/// <param name="PackageId">包 id。</param>
/// <param name="PackageRoot">正式包根（新建模式＝最终目录，尚未存在）。</param>
/// <param name="TemporaryRoot">临时包目录（<c>&lt;包目录&gt;.tmp</c>）。</param>
/// <param name="IsNewPackage">是否新建包。</param>
/// <param name="PackageGuid">包稳定身份。</param>
/// <param name="DependsOnPackages">期望依赖的卡包 ID（新建模式）。</param>
/// <param name="AssetCount">当前声明的资产数。</param>
/// <param name="Warnings">会话告警（如依赖包未找到；不阻断）。</param>
public sealed record EditorSessionInfo(
    string PackageId,
    string PackageRoot,
    string TemporaryRoot,
    bool IsNewPackage,
    Guid PackageGuid,
    IReadOnlyList<string> DependsOnPackages,
    int AssetCount,
    IReadOnlyList<string> Warnings,
    int RequiredDeckSize = 0);

/// <summary>`asset.list` / `asset.get` 条目（R9）。</summary>
/// <param name="Kind">资产种类。</param>
/// <param name="PackageId">所属包 id。</param>
/// <param name="LocalId">包内局部 ID。</param>
/// <param name="AppId">应用层 ID（<c>pack/{包id}/{局部ID}</c>）。</param>
/// <param name="Version">资产版本（测试域＝正式版追加测试段）。</param>
/// <param name="AssetGuid">资产版本 GUID（由身份派生）。</param>
/// <param name="SourcePath">源文件相对包根路径（<c>/</c> 分隔）。</param>
/// <param name="SidecarPath">sidecar 相对包根路径（<c>/</c> 分隔）。</param>
/// <param name="IsNew">是否本会话新建（无正式版基线）。</param>
/// <param name="Tags">卡牌标签（非卡资产＝空；用于 R9 的 tag 过滤）。</param>
public sealed record EditorAssetInfo(
    AssetKind Kind,
    string PackageId,
    string LocalId,
    string AppId,
    AssetVersion Version,
    Guid AssetGuid,
    string SourcePath,
    string SidecarPath,
    bool IsNew,
    IReadOnlyList<string> Tags);

/// <summary>`asset.commit` 的产出。</summary>
/// <param name="Asset">
/// 提交后的资产（新版本／新 GUID）。**提交非法卡组被拒时为 null**——那时没有任何东西被写入，
/// 硬编一个「提交结果」只会误导调用方。
/// </param>
/// <param name="VersionBumped">是否由本次提交自动递增（<c>version.set</c>/<c>asset.create</c> 后首次提交＝false）。</param>
public sealed record EditorCommitResult(EditorAssetInfo? Asset, bool VersionBumped)
{
    /// <summary>
    /// 卡组内容违规（R7/D43）：仅「提交非法卡组被拒」时非空——源文件保持原样、声明不落盘、版本不递增。
    /// <para>
    /// 与注册期共用 <see cref="Orc.Game.Decks.DeckViolation"/>：客户端按 <c>Kind</c> 分类、按 <c>CardId</c> 点名。
    /// 这里**不**返回 <see cref="AssetDiagnostic"/>（提交期不涉及包注册，硬套 PackageId／File 只能编造字段）。
    /// </para>
    /// </summary>
    public IReadOnlyList<Orc.Game.Decks.DeckViolation>? Violations { get; init; }
}

/// <summary>`lock.preview` 的产出：转正后的正式 lock（预测）＋ 当前测试 lock（实况）。</summary>
/// <param name="Formal">转正将写出的正式 lock。</param>
/// <param name="Test">当前临时包的测试 lock（已自动重算）。</param>
public sealed record EditorLockPreview(AssetLock Formal, AssetLock Test);

/// <summary>`promote` / `session.end(save)` 的产出。</summary>
/// <param name="PackageId">包 id。</param>
/// <param name="PackageRoot">正式包根。</param>
/// <param name="IsNewPackage">是否新建包（目录由临时包转正生成）。</param>
/// <param name="Assets">转正后的资产。</param>
/// <param name="FormalLock">写出的正式 lock。</param>
/// <param name="TestLock">写出的测试 lock（转正后为空）。</param>
/// <param name="ArchivedVersions">归档的历史版本目录（<c>versions/&lt;版本&gt;</c>；未归档＝空）。</param>
public sealed record EditorPromotionResult(
    string PackageId,
    string PackageRoot,
    bool IsNewPackage,
    IReadOnlyList<EditorAssetInfo> Assets,
    AssetLock FormalLock,
    AssetLock TestLock,
    IReadOnlyList<string> ArchivedVersions);

/// <summary>`reload` 的产出。</summary>
/// <param name="Configured">是否注入了装载器（未注入＝只做计划缓存，见 <see cref="AssetLoader"/>）。</param>
/// <param name="Result">装载结果（结构化，不抛错）。</param>
public sealed record EditorReloadResult(bool Configured, AssetLoadResult Result);

/// <summary>
/// 结构化资产过滤（R9/D57 双轨的「工具面」）：lambda 无法跨进程，故工具面用本条件集；
/// 进程内仍可直接传 <see cref="Func{T,TResult}"/>（两者在同一后端组件内求值）。
/// </summary>
public sealed record EditorAssetQuery
{
    /// <summary>种类过滤（null＝不限）。</summary>
    public AssetKind? Kind { get; init; }

    /// <summary>包 id 过滤（null＝不限）。</summary>
    public string? PackageId { get; init; }

    /// <summary>局部 ID 前缀（null／空白＝不限）。</summary>
    public string? IdPrefix { get; init; }

    /// <summary>标签过滤（全部命中；空＝不限）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>版本下界（含；null＝不限）。</summary>
    public AssetVersion? MinVersion { get; init; }

    /// <summary>版本上界（含；null＝不限）。</summary>
    public AssetVersion? MaxVersion { get; init; }

    /// <summary>是否命中该资产。</summary>
    /// <param name="asset">待判定资产。</param>
    /// <exception cref="ArgumentNullException">asset 为 null。</exception>
    public bool Matches(EditorAssetInfo asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (Kind is { } kind && asset.Kind != kind)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(PackageId)
            && !string.Equals(asset.PackageId, PackageId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(IdPrefix)
            && !asset.LocalId.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (MinVersion is { } min && asset.Version < min)
        {
            return false;
        }

        if (MaxVersion is { } max && asset.Version > max)
        {
            return false;
        }

        foreach (var tag in Tags)
        {
            if (!asset.Tags.Contains(tag, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>后端编辑器可选项（策略仍是**必填**构造参数，不在此处）。</summary>
public sealed record AssetEditorOptions
{
    /// <summary>可编辑包根（`pack.list` 的枚举基；空＝用策略授权根）。</summary>
    public IReadOnlyList<string> PackageRoots { get; init; } = Array.Empty<string>();

    /// <summary>装载器（`reload` 用；未注入＝用注册表新建一个只计算不落地的装载器）。</summary>
    public AssetLoader? Loader { get; init; }

    /// <summary>
    /// 卡组要求的张数（R7/D11/D15）：提交期判定与注册期必须同一口径，故缺省值与
    /// <see cref="Orc.Game.Decks.DeckRules.DefaultRequiredSize"/> 对齐，并在 <c>session.begin</c> 回显给作者。
    /// </summary>
    public int RequiredDeckSize { get; init; } = Orc.Game.Decks.DeckRules.DefaultRequiredSize;
}
