using Orc.Core.Model;

namespace Orc.Packages.Editing;

/// <summary>
/// **`named` 的虚拟 moding 记录**（R39 裁决 13／S30）：有 id、可预览、可实例化看效果，
/// 但 **<c>Save</c> 之前不在 <see cref="PrefabCatalog"/> 的正式解析面**——卡数据引用它＝明确报错。
/// <para>
/// 三阶段同一种 <see cref="AssetKind.Effect"/>（不新增 <c>AssetKind</c>）：虚拟记录（仅会话）→
/// <c>Save</c> 后（资产库 ＋ 测试 lock）→ 转正（正式 lock）。差别只在段位与可见面，不在种类。
/// </para>
/// </summary>
/// <param name="Id">调用方给的资产 id（<c>Save</c> 时原样落盘，不变）。</param>
/// <param name="Asset">展开后的自包含资产（饱和；带 <c>slots[].value</c> 与注入的私有字段）。</param>
/// <param name="Type">展开后的预制体类型（HUD／预览用，与 <c>inline</c> 形态一致）。</param>
/// <param name="Origin">出身记录（<c>templateId</c>／<c>argsHash</c>／<c>args</c>）。</param>
/// <param name="ProductJson"><c>Save</c> 时原样写盘的产品文本（<see cref="AssetSchema.Write"/>）。</param>
public sealed record NamedInstanceRecord(
    string Id,
    ComponentAsset Asset,
    PrefabType Type,
    AssetOrigin Origin,
    string ProductJson);
