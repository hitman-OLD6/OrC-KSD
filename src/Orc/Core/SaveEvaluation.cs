using Orc.Core.Model;

namespace Orc.Core;

/// <summary>
/// 存档条目（本会话 S5）：一个可存档根实体与其 moding——审查导出可选段
/// <c>savedEntities: [{ name, moding }]</c> 的条目（<see cref="AuditChain.SavedEntities"/>）。
/// <para><paramref name="Name"/>＝实体名（恢复时的实例名；同名恢复即失败、不加后缀——裁决 9）。</para>
/// </summary>
public sealed record SavedEntityRecord(string Name, SaveModing Moding);

/// <summary>
/// 存档求值核（本会话 S3–S5；internal——公开入口是 <see cref="LogicEngine.ExportSave"/>）：
/// 枚举存档单位、逐实体解析服务并求值，**先收集完所有条目、再序列化**（一次性求值，D56）。
/// <para>
/// 失败语义（Q5／D50／D51）：**失败即抛、不回退、不跳过**——
/// 源不在目录（必要依赖）／服务未注册／注册表未装配／导出器返回 null，任一命中即整次存档失败。
/// 唯一的「非失败排除」是门户缺席：实体（或其源）未声明 <c>save</c> ⇒ 不可存档，快照不写该实体。
/// </para>
/// </summary>
internal static class SaveEvaluation
{
    /// <summary>
    /// 枚举并求值全部存档单位（D54 三条判据同时成立）：
    /// <c>Host == null</c>（主判据——有宿主的实体状态由宿主 attachments 承载）∧
    /// <c>EntityKind != Engine</c>（引擎实体由新运行时自动建）∧
    /// 非 <c>legacy.</c> 框架合成实体（<see cref="FrameworkEntityPolicy.IsSynthetic"/>，判据集中一处）。
    /// 每实体只求值一次；门户缺席的实体不进结果（缺席＝「没存」，与「存了但空」可区分——S5）。
    /// </summary>
    internal static IReadOnlyList<SavedEntityRecord> EvaluateRoots(
        OrchestrationRuntime runtime, JudicatorRegistry? judicators)
    {
        GuardAgainstOrphans(runtime);

        var context = new SaveContext(runtime, judicators);
        var results = new List<SavedEntityRecord>();
        var evaluated = new HashSet<OrchestrationEntity>(ReferenceEqualityComparer.Instance);

        foreach (var entity in runtime.Entities)
        {
            if (entity.Host is not null
                || entity.EntityKind == EntityKind.Engine
                || FrameworkEntityPolicy.IsSynthetic(entity.Type.Id))
            {
                continue;
            }

            if (!evaluated.Add(entity))
            {
                continue; // 每实体只求值一次（枚举理论上无重复；防御性去重，代价为零）
            }

            var moding = TryExportRoot(entity, context, judicators);
            if (moding is null)
            {
                continue; // 门户缺席（D50）：未声明 save ＝ 不可存档，快照不写该实体
            }

            results.Add(new SavedEntityRecord(entity.Name, moding));
        }

        return results;
    }

    /// <summary>
    /// **存档侧孤儿守卫**（S6／遗留 1）：任何有宿主的实体必须在某宿主的附件集里——
    /// 否则它的状态既不进 <c>savedEntities</c>（<see cref="OrchestrationEntity.Host"/> 非 null 被判据排除）
    /// 也不进任何宿主的 <c>attachments</c>（不在装配面）＝**静默丢失**。
    /// <para>
    /// 成因只有一条：直接 <c>CreateEntity(id, name, host)</c> 绕过组件装配面（正常路径
    /// <c>ConstructComponent</c>／<c>GrantComponent</c> 必登记 <c>_componentSets</c>）。
    /// 与恢复侧 <c>OrchestrationRuntime.EnsureNoOrphans</c> 构成双向守卫：存档拒绝「直建宿主实体」，
    /// 恢复拒绝「孤儿」——两侧都显式失败，绝不沉默丢掉一份状态。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">存在有宿主却不在任何附件集里的实体。</exception>
    private static void GuardAgainstOrphans(OrchestrationRuntime runtime)
    {
        var attached = new HashSet<OrchestrationEntity>(
            runtime.Components.Select(static attachment => attachment.Component),
            ReferenceEqualityComparer.Instance);
        var orphans = runtime.Entities
            .Where(entity => entity.Host is not null && !attached.Contains(entity))
            .ToList();
        if (orphans.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "存档失败：以下实体有宿主却不在任何宿主的附件集里——其状态既不会被存档（有宿主被排除）、"
            + "恢复也无法重建（不在 attachments 面）："
            + string.Join("、", orphans.Select(static o => $"'{o.Name}'(预制体 '{o.Type.Id}'，宿主 '{o.Host!.Name}')"))
            + "。成因＝直接 CreateEntity(id, name, host) 绕过组件装配面；请改走 ConstructComponent／GrantComponent。");
    }

    /// <summary>求值一个**根**实体：门户缺席 ⇒ null（排除）；其余失败即抛。</summary>
    internal static SaveModing? TryExportRoot(
        OrchestrationEntity entity, SaveContext context, JudicatorRegistry? judicators)
        => ExportCore(entity, context, judicators, host: null, portalRequired: false);

    /// <summary>
    /// 求值一个**附件**（S4）：门户看附件的源（同宿主公式、D52）；门户缺席 ⇒ **宿主存档失败**（Q2）。
    /// </summary>
    internal static SaveModing ExportAttachment(
        OrchestrationEntity component, SaveContext context, JudicatorRegistry? judicators)
        => ExportCore(component, context, judicators, host: component.Host, portalRequired: true)
            ?? throw new InvalidOperationException(
                $"存档失败：实体 '{component.Host!.Name}' 的附件 '{SaveContext.ExtendsOf(component)}'（实体 "
                + $"'{component.Name}'）的导出器返回 null ⇒ 宿主存档失败（Q5：返回 null＝整体失败，不跳过）。");

    /// <summary>
    /// 求值核（根与附件共用——「复用 S3 的服务解析路径」，S4）：源解析 → 门户读取 → 服务解析 → 条目调用。
    /// </summary>
    /// <param name="host">宿主（附件求值时用于错误文案定位；根求值＝null）。</param>
    /// <param name="portalRequired">true＝门户缺席即抛（附件）；false＝门户缺席返回 null（根，排除）。</param>
    private static SaveModing? ExportCore(
        OrchestrationEntity entity, SaveContext context, JudicatorRegistry? judicators,
        OrchestrationEntity? host, bool portalRequired)
    {
        // 溯源唯一入口（D51／D52）：extends ＝ type.Origin?.TemplateId ?? type.Id——缓存键与产物资产 id 绝不进来。
        var extends = SaveContext.ExtendsOf(entity);

        PrefabType source;
        try
        {
            source = context.Runtime.Resolve(extends);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"存档失败：实体 '{entity.Name}' 的源预制体 '{extends}' 不在目录里——必要依赖"
                + "（没有源就无法展开，恢复本来也做不到；D51：这不是「origin 变成依赖」）。", ex);
        }

        var serviceId = source.Save;
        if (serviceId is null)
        {
            if (!portalRequired)
            {
                return null; // 门户缺席（D50）：未声明 save ＝ 不可存档（快照不写该实体）——唯一的非失败排除
            }

            throw new InvalidOperationException(
                $"存档失败：实体 '{host!.Name}' 的附件 '{source.Id}'（实体 '{entity.Name}'）未声明 save "
                + "⇒ 宿主存档失败（Q2：组件的 save 服务于「能否被嵌入」，不跳过）。");
        }

        if (judicators is null)
        {
            throw new InvalidOperationException(
                $"存档失败：本对局未装配判定器注册表（Judicators == null）——实体 '{entity.Name}' 声明的 "
                + $"save 服务 '{serviceId}' 无法解析（三态失败之一：注册表缺失 ≠ 服务未注册）。");
        }

        Orc.Core.JudicatorRegistration registration;
        try
        {
            registration = judicators.Resolve(serviceId);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"存档失败：实体 '{entity.Name}' 声明的 save 服务 '{serviceId}' 未注册 ⇒ 存档失败"
                + "（不自动兜底、不跳过——D50：「插件包没加载」不得变成静默降级）。", ex);
        }

        // 经条目统一解析点调用：moding 改写（RegisterReplaceWrapper）随之生效（S3 验收）。
        var result = registration.Invoke(new object[] { entity, context });

        SaveModing? moding;
        if (result is null || result.Length == 0 || result[0] is null)
        {
            throw new InvalidOperationException(
                $"存档失败：实体 '{entity.Name}' 的存档导出器 '{serviceId}' 返回 null ⇒ 整个存档失败"
                + "（Q5：少一个实体的存档就是另一个对局）。");
        }

        moding = result[0] as SaveModing;
        if (moding is null)
        {
            throw new InvalidOperationException(
                $"存档失败：实体 '{entity.Name}' 的存档导出器 '{serviceId}' 返回了 "
                + $"{result[0]!.GetType().Name}（期望 SaveModing）——契约不符（fail-fast）。");
        }

        ValidateExtends(entity, moding, serviceId);
        return moding;
    }

    /// <summary>
    /// 形态校验（裁决 1 第一条／S2 验收「extends 指向非祖先 ⇒ 拒绝」）：
    /// <see cref="SaveModing.Extends"/> 必须是本实体的类型 id、<c>extends</c> 链上的祖先，或源模板 id
    /// （inline／named 产物的结构锚点＝<see cref="PrefabType.Origin"/> 的 template id）。
    /// </summary>
    private static void ValidateExtends(OrchestrationEntity entity, SaveModing moding, string serviceId)
    {
        var self = entity.Type.Id;
        var origin = entity.Type.Origin?.TemplateId;
        if (string.Equals(moding.Extends, self, StringComparison.Ordinal)
            || entity.Type.Chain.Contains(moding.Extends)
            || (origin is not null && string.Equals(moding.Extends, origin, StringComparison.Ordinal)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"存档失败：实体 '{entity.Name}' 的存档导出器 '{serviceId}' 返回的 moding.extends "
            + $"'{moding.Extends}' 既不是实体自身预制体（'{self}'）、也不是它的祖先或源模板"
            + $"{(origin is null ? "" : $"（'{origin}'）")}——「引回对应预制体」没有锚点（裁决 1／S2）。");
    }
}
