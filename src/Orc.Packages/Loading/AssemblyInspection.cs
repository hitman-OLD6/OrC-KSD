using Orc.Packages;

namespace Orc.Packages.Loading;

/// <summary>
/// 单个卡包目录在「判定结论」里的行（**由集合条目逐行构造**；含失效条目）。
/// <para>
/// 有效行的 <see cref="Ok"/> 与装载摘要来自 <see cref="MatchCardAssembly.Inspect"/>；
/// 失效行不入装配计算，直接给 <see cref="Ok"/>=false ＋ <see cref="ErrorCode"/> ＋ <see cref="WillRemove"/>=true。
/// </para>
/// <para>
/// <see cref="ErrorCode"/> 取值见 <see cref="PackageProbeCodes"/>；<see cref="PackageProbeCodes.ManifestInvalid"/>
/// 也覆盖「包内结构性非法（sidecar 不可解析、GUID 重复…）」——两者都意味着该包不可装载。
/// </para>
/// </summary>
/// <param name="Directory">包目录（绝对路径；受众裁剪由投影负责）。</param>
/// <param name="PackageId">包 id（读盘快照；未知＝null）。</param>
/// <param name="Version">包版本（内容指纹；未知＝null）。</param>
/// <param name="Guid">包稳定身份（未知＝null）。</param>
/// <param name="Ok">该包本身是否可装载。</param>
/// <param name="ErrorCode">失败码（成功＝null）。</param>
/// <param name="Error">人读原因（成功＝null；自由文本，仅管理面与 sandbox 受众可见）。</param>
/// <param name="WillRemove">是否将在下一次写面或开局的对账中被移除（读面的预演标记）。</param>
public sealed record PackageInspection(
    string Directory,
    string? PackageId,
    string? Version,
    string? Guid,
    bool Ok,
    string? ErrorCode = null,
    string? Error = null,
    bool WillRemove = false)
{
    /// <summary>
    /// 该包的**卡组诊断**（R5/D42）：落在这里而不是 <see cref="AssemblyInspection"/> 的顶层段——
    /// 后者表达「这次开局会怎样」，而「卡组合不合格」属注册面事实、按包归属。
    /// 合法的包＝空。
    /// </summary>
    public IReadOnlyList<AssetDiagnostic> DeckDiagnostics { get; init; }
        = Array.Empty<AssetDiagnostic>();
}

/// <summary>
/// 装载计划摘要（只给数字，不给完整卡清单）。
/// </summary>
/// <param name="DefinitionCount">装到的卡定义数。</param>
/// <param name="FormalLayers">正式域层数。</param>
/// <param name="TestLayers">测试域层数。</param>
/// <param name="ItemCount">两域拓扑节点总数（不含内含资产）。</param>
/// <param name="CardCount">两域中的卡资产节点数。</param>
public sealed record AssemblyPlanSummary(
    int DefinitionCount,
    int FormalLayers,
    int TestLayers,
    int ItemCount,
    int CardCount)
{
    /// <summary>全零摘要（空池／未产出计划）。</summary>
    public static readonly AssemblyPlanSummary Zero = new(0, 0, 0, 0, 0);
}

/// <summary>
/// **装配判定结论**（唯一模型）：回答「现在按这份目录集合开局会怎样」。
/// <para>
/// 与 <see cref="MatchCardAssembly.Register"/> **同一套判定实现**（见 <see cref="MatchCardAssembly.Inspect"/>），
/// 因此不存在「试算通过、开局失败」的第二套规则。
/// </para>
/// <para>
/// <see cref="Ok"/>＝现在开局会不会成功；健康性更细的判据（是否阻断一次集合变更）由 <see cref="AssetLoadIssue.IsFatal"/>
/// 表达——空池是 <c>Ok=false</c> 但 <c>fatal=false</c>。
/// </para>
/// </summary>
/// <param name="Ok">按此输入开局是否可成功。</param>
/// <param name="Packages">逐包行（输入顺序＝集合注册序）。</param>
/// <param name="Issues">计算期问题（结构化）。</param>
/// <param name="Warnings">人读告警（自由文本，仅管理面与 sandbox 受众可见）。</param>
/// <param name="Plan">装载计划摘要。</param>
public sealed record AssemblyInspection(
    bool Ok,
    IReadOnlyList<PackageInspection> Packages,
    IReadOnlyList<AssetLoadIssue> Issues,
    IReadOnlyList<string> Warnings,
    AssemblyPlanSummary Plan);
