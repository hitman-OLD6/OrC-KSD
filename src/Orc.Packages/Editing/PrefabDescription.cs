using Orc.Core.Model;

namespace Orc.Packages.Editing;

/// <summary>
/// **预制体视图**（R4 扩展／R24／S10 入口一）：一次只读查询给出画布需要的**全部**编辑面——
/// 展开后的成员表（标注继承／本层）、每成员视图字段与可写性、可重载性、已有挂载、
/// 可挂载信号清单（带 <c>timing</c>）、可包装成员与相位（带可写字段）。
/// <para>
/// **纯数据**（JSON 可序列化、无运行期对象引用，R4 第 2 条／R24 第 2 条）；前端**不解析父资产文件**
/// （R24 第 2 条），也不需要自己推导继承。
/// </para>
/// <para>
/// **与装载期展开同源**（R4 第 3 条／R24 第 4 条）：数据全部来自 <see cref="PrefabCatalog.Resolve"/> 的
/// 展开结果（<see cref="PrefabType"/>）＋资产声明里的 handler 来源，编辑器侧**没有第二套展开**。
/// </para>
/// <para>
/// **无草案与有草案返回同一结构**（D13）：<see cref="Draft"/> 为 null vs 有内容，前端一种解析路径、不需要分支。
/// </para>
/// </summary>
/// <param name="PackRoot">包根（归一后的绝对路径）。</param>
/// <param name="PrefabAssetId">父资产 id（查询起点）。</param>
/// <param name="Prefab">父资产摘要；**父未注册＝null**（同时给 <see cref="Diagnostics"/>，不抛异常）。</param>
/// <param name="Draft">草案摘要（无草案＝null）。</param>
/// <param name="Members">展开后的成员表（声明序＝父先 ＋ 子追加、覆写原位替换，与装载期一致）。</param>
/// <param name="MountableTargets">可挂载目标（信号清单；每条带 <see cref="PrefabMountableTarget.Timing"/>）。</param>
/// <param name="WrappableMembers">可包装成员与相位（含可写字段，与视图声明同源）。</param>
/// <param name="Diagnostics">诊断（父未注册、草案缺失、草案父不一致等）。</param>
/// <param name="Stage">
/// **身份阶段**（R39 裁决 13／S33）：<see cref="PrefabStage.Virtual"/>＝编辑器会话里的未保存 names 记录；
/// <see cref="PrefabStage.Test"/>＝已 <c>Save</c>（测试 lock）；<see cref="PrefabStage.Formal"/>＝已转正（正式 lock）。
/// 前端据此显示「未保存」并禁用「被卡数据引用」相关入口。
/// </param>
public sealed record PrefabDescription(
    string PackRoot,
    string PrefabAssetId,
    PrefabSummary? Prefab,
    PrefabDraftSummary? Draft,
    IReadOnlyList<PrefabMemberDescription> Members,
    IReadOnlyList<PrefabMountableTarget> MountableTargets,
    IReadOnlyList<PrefabWrappableMember> WrappableMembers,
    IReadOnlyList<PrefabDescriptionDiagnostic> Diagnostics,
    PrefabStage Stage = PrefabStage.Formal);

/// <summary>预制体在资产体系里的**身份阶段**（R39 裁决 13）：三态只影响可见面与能否被引用，不影响形态。</summary>
public enum PrefabStage
{
    /// <summary>虚拟记录：只在编辑器会话内可见，**不可被卡数据引用**（`PrefabCatalog` 正式解析面不认）。</summary>
    Virtual,

    /// <summary>已保存（测试段）：进资产库与测试 lock，测试段可引用。</summary>
    Test,

    /// <summary>已转正（正式段）：进正式 lock，可被正式卡数据引用。</summary>
    Formal,
}

/// <summary>父资产摘要（展开后的预制体层信息；纯数据）。</summary>
/// <param name="Id">预制体 id。</param>
/// <param name="Kind">实体类别（<c>component</c>／<c>card</c>／<c>engine</c>…）。</param>
/// <param name="Layer">来源层（<c>kernel</c>／<c>game</c>／<c>asset</c>／<c>runtime</c>）。</param>
/// <param name="Chain">继承链（根→叶子）。</param>
/// <param name="RequiresHost">宿主需求。</param>
/// <param name="ActiveWhen">装配时点（<c>loaded</c>／<c>placed</c>）。</param>
/// <param name="OneShot">是否一次性组件。</param>
/// <param name="DisposalSignal">兜底信号。</param>
/// <param name="IsTemplate">
/// 是否**模板**（R39 裁决 4；＝存在未填空位，S28 的合并谓词）：前端据此显示「模板」标记、
/// 允许「从模板新建」而不是「打开已有资产改名」。成员级的「可覆写」看
/// <see cref="PrefabMemberDescription.Overridable"/>——**没有资产级开关字段**（R39 裁决 5 已废止）。
/// </param>
/// <param name="Slots">合并后的空位 schema（S33：父链声明的资产级空位对表单可见）。</param>
/// <param name="Origin">出身记录（S31；null＝手写资产）：仅供追溯／编辑器回填，不是依赖。</param>
/// <param name="Label">展示名（R35；null＝未声明，前端回退显示 <see cref="Id"/>）。</param>
/// <param name="Description">展示描述（R35；null＝未声明）。</param>
public sealed record PrefabSummary(
    string Id,
    string Kind,
    string Layer,
    IReadOnlyList<string> Chain,
    string RequiresHost,
    string ActiveWhen,
    bool OneShot,
    string DisposalSignal,
    bool IsTemplate = false,
    IReadOnlyList<PrefabSlotDescription>? Slots = null,
    AssetOrigin? Origin = null,
    string? Label = null,
    string? Description = null);

/// <summary>空位 schema 的一行（S33；纯数据，供编辑器／前端表单消费）。</summary>
/// <param name="Name">键名（资产级＝空位名；成员级＝成员名）。</param>
/// <param name="Type">类型（<c>number</c>／<c>string</c>／<c>bool</c>／<c>enum</c>／<c>asset-ref</c>／<c>handler</c>）。</param>
/// <param name="Scope">作用域（<c>asset</c>／<c>member</c>；仅展示用，<c>args</c> 本身是扁平表）。</param>
/// <param name="Sealed">是否禁止再填。</param>
/// <param name="Filled">是否已填（声明期值／成员 handler）。</param>
/// <param name="RuntimeEffect">是否影响运行期行为（<c>asset-ref</c>＝false，R39 裁决 15）。</param>
/// <param name="EnumValues"><c>enum</c> 取值集。</param>
public sealed record PrefabSlotDescription(
    string Name,
    string Type,
    string Scope,
    bool Sealed,
    bool Filled,
    bool RuntimeEffect,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>草案摘要（有草案＝非 null；**只报增量**，展开表由查询现算，D13）。</summary>
/// <param name="Path">草案相对包根的路径（<c>/</c> 分隔）。</param>
/// <param name="DraftAssetId">草案自身的资产 id（≠ 父 id）。</param>
/// <param name="DeclaredParentId">草案声明的父 id（与查询的父不一致＝诊断）。</param>
/// <param name="OverriddenMembers">被重载的成员名（草案声明且父里已存在）。</param>
/// <param name="AddedMembers">新增的成员名（草案声明且父里没有）。</param>
/// <param name="Mounts">草案声明的挂载（<c>作用域#成员名</c> → 成员名）。</param>
/// <param name="Wraps">草案声明的包装（目标地址＋相位）。</param>
public sealed record PrefabDraftSummary(
    string Path,
    string DraftAssetId,
    string? DeclaredParentId,
    IReadOnlyList<string> OverriddenMembers,
    IReadOnlyList<string> AddedMembers,
    IReadOnlyList<PrefabDeclarationSummary> Mounts,
    IReadOnlyList<PrefabWrapSummary> Wraps);

/// <summary>一条声明摘要（挂载用：目标地址 ＋ 被挂载的自身成员）。</summary>
/// <param name="Target">目标地址（<c>作用域#成员名</c>）。</param>
/// <param name="Member">被挂载／被包装的成员名。</param>
public sealed record PrefabDeclarationSummary(string Target, string Member);

/// <summary>一条包装声明摘要。</summary>
/// <param name="Target">目标地址（<c>作用域#成员名</c>）。</param>
/// <param name="Phase">相位（<c>before</c>／<c>after</c>／<c>replace</c>）。</param>
/// <param name="Seam">具名缝（null＝成员外围）。</param>
public sealed record PrefabWrapSummary(string Target, string Phase, string? Seam);

/// <summary>
/// 展开后的一个成员（R24 第 1 条）。**继承／本层标注**由 <see cref="Inherited"/>／<see cref="Overridden"/>／
/// <see cref="Added"/> 三个布尔给出，前端不需要自己推导继承链。
/// </summary>
/// <param name="Name">成员名。</param>
/// <param name="Address">成员地址（<c>self#成员名</c>；本资产自身视角）。</param>
/// <param name="Inherited">是否继承（**本层＝草案**：无草案时全部为 true；有草案时只有未重载的成员为 true）。</param>
/// <param name="Overridden">是否被草案重载（草案声明且父里已存在；整段替换）。</param>
/// <param name="Added">是否由草案新增（父里没有）。</param>
/// <param name="DeclaredBy">声明该成员的预制体 id（展开期信息；null＝引擎内建代码声明的成员）。</param>
/// <param name="Visibility">可见性位（<c>callable</c>／<c>hostable</c>／<c>mountable</c>；空＝私有）。</param>
/// <param name="IsSignal">是否信号成员（时机入口）。</param>
/// <param name="SignalTiming">信号时机（<c>any</c>／<c>load</c>／<c>placed</c>；非信号＝null）。</param>
/// <param name="Sealed">成员级封死（R2）。</param>
/// <param name="RequiresImplementation">
/// 该成员**必须由子实现**（R34 第 2 条／S32：＝ <c>slot ∧ ¬handler</c>）：前端据此把未实现成员标红并拦住摊平。
/// <para>与 <see cref="Overridable"/> 是两件事：前者＝「父是抽象、必须填」，后者＝「允许覆写、可选改」。</para>
/// </param>
/// <param name="Overridable">
/// 本次是否允许被覆写（R39 裁决 5／D39）：**本成员**在链上声明了 <c>slot</c> ∧ 未 <c>sealed</c>
/// （<c>framework.</c> 前缀恒 sealed）。与「有没有 handler」无关——判定表第 1、2 行都为 true。
/// </param>
/// <param name="SeamNames">handler 内部的具名插入点。</param>
/// <param name="ViewFields">视图字段（名／类型／可写／必填）。</param>
/// <param name="HandlerSource">handler 来源（<c>script</c>／<c>dsl</c>／<c>assembly-key</c>；无 handler＝null）。</param>
/// <param name="HandlerId">handler 资产 id（无 handler＝null）。</param>
/// <param name="MountedTo">本成员被挂到哪些目标地址（已有挂载）。</param>
/// <param name="WrappedAt">本成员被哪些包装声明指向（目标地址＋相位）。</param>
/// <param name="Label">展示名（R35；null＝未声明，前端回退显示 <see cref="Name"/>）。</param>
public sealed record PrefabMemberDescription(
    string Name,
    string Address,
    bool Inherited,
    bool Overridden,
    bool Added,
    string? DeclaredBy,
    IReadOnlyList<string> Visibility,
    bool IsSignal,
    string? SignalTiming,
    bool Sealed,
    bool Overridable,
    IReadOnlyList<string> SeamNames,
    IReadOnlyList<PrefabViewFieldDescription> ViewFields,
    string? HandlerSource,
    string? HandlerId,
    IReadOnlyList<string> MountedTo,
    IReadOnlyList<PrefabWrapSummary> WrappedAt,
    bool RequiresImplementation = false,
    string? Label = null);

/// <summary>视图字段声明（纯数据；<see cref="Writable"/> 是 <c>write</c> 原语与可写字段清单的**唯一**依据）。</summary>
/// <param name="Name">字段名。</param>
/// <param name="Type">封闭类型名（<c>entity</c>／<c>number</c>／<c>string</c>／<c>boolean</c> 及其 <c>[]</c> 形态）。</param>
/// <param name="Writable">是否可写。</param>
/// <param name="Required">是否必填。</param>
public sealed record PrefabViewFieldDescription(string Name, string Type, bool Writable, bool Required);

/// <summary>
/// 可挂载目标（R24 第 1 条的最后两项之一）：**信号成员清单**。
/// <para>
/// <see cref="Timing"/> **必须带**（D13）：否则前端会把 <c>load</c> 时机的信号挂给 <c>placed</c> 组件，
/// 装载期才被拒（`active-when: placed` 的组件挂加载期信号＝装载期拒绝）。
/// </para>
/// </summary>
/// <param name="Address">目标地址（<c>engine#信号名</c>／<c>host#信号名</c>）。</param>
/// <param name="Member">信号成员名。</param>
/// <param name="Timing">信号时机（<c>any</c>／<c>load</c>／<c>placed</c>）。</param>
/// <param name="Owner">声明该信号的预制体 id。</param>
/// <param name="ViewFields">该成员的视图字段（被挂载成员需要的输入，前端据此连线）。</param>
public sealed record PrefabMountableTarget(
    string Address,
    string Member,
    string Timing,
    string Owner,
    IReadOnlyList<PrefabViewFieldDescription> ViewFields);

/// <summary>
/// 可包装成员与相位（R24 第 1 条的最后一项）。
/// <para>
/// <see cref="WritableFields"/> 与 <see cref="PrefabMemberDescription.ViewFields"/> 的 <c>writable</c> **同源**
/// （都读同一份 <see cref="ViewSpec"/>）——它是 R17 <c>write</c> 原语的前端依据：不可写字段清单与可写清单
/// 出现分歧时，图校验会拒掉编辑器显示为可写的字段。
/// </para>
/// </summary>
/// <param name="Address">成员地址（<c>self#…</c>／<c>engine#…</c>）。</param>
/// <param name="Member">成员名。</param>
/// <param name="SupportedPhases">
/// 该成员实际允许的相位（<c>before</c>／<c>after</c>／<c>replace</c>）。
/// 成员目标的三种相位都成立；<see cref="SeamNames"/> 里的缝只允许 <c>before</c>（缝在 handler 内部）。
/// </param>
/// <param name="SeamNames">具名缝（包装可挂到 <c>(成员, 缝名)</c> 上，相位恒 <c>before</c>）。</param>
/// <param name="WritableFields">可写视图字段名（与视图声明同源；R17 的 <c>write</c> 白名单）。</param>
public sealed record PrefabWrappableMember(
    string Address,
    string Member,
    IReadOnlyList<string> SupportedPhases,
    IReadOnlyList<string> SeamNames,
    IReadOnlyList<string> WritableFields);

/// <summary>
/// 诊断（**不抛异常**，R1 第 2 条／D13）：父未注册、草案缺失／不一致、资产不可解析等。
/// </summary>
/// <param name="Code">机器可读码（<c>pack-missing</c>／<c>parent-missing</c>／<c>parent-invalid</c>／<c>draft-missing</c>／<c>draft-parent-mismatch</c>／<c>asset-invalid</c>／<c>template-root-missing</c>）。</param>
/// <param name="Target">出错对象（包根／资产 id／草案路径）。</param>
/// <param name="Message">人类可读文案。</param>
public sealed record PrefabDescriptionDiagnostic(string Code, string Target, string Message)
{
    /// <summary>包根不存在。</summary>
    public const string PackMissing = "pack-missing";

    /// <summary>父资产未注册（R1 第 2 条）。</summary>
    public const string ParentMissing = "parent-missing";

    /// <summary>父资产存在但展开失败（结构错误）。</summary>
    public const string ParentInvalid = "parent-invalid";

    /// <summary>草案路径给了但文件不存在。</summary>
    public const string DraftMissing = "draft-missing";

    /// <summary>草案声明的父与查询的父不一致。</summary>
    public const string DraftParentMismatch = "draft-parent-mismatch";

    /// <summary>包内某份资产无法解析（不阻断其余资产的展开）。</summary>
    public const string AssetInvalid = "asset-invalid";

    /// <summary>
    /// **模板库根路径未配置**（R34 第 4 条／D30 第 7 个码）：查询的是 <c>template.</c> 前缀的 id，
    /// 但 <see cref="AssetEditor"/> 构造时没传模板库根。
    /// <para>
    /// 与 <see cref="ParentMissing"/> 分开的理由：两者同形同码会让前端**永远不知道该配路径**——
    /// 「父未注册」是资产问题，「模板库未配置」是配置问题，修复动作完全不同。
    /// </para>
    /// </summary>
    public const string TemplateRootMissing = "template-root-missing";
}
