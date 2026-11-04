using Orc.Core;

namespace Orc.Core.Model;

/// <summary>
/// <see cref="OrchestrationRuntime"/> 的**恢复面**（本会话 S6／裁决 9／D57／D58；partial）：
/// 存档 moding → 重建实体。两阶段：
/// <list type="number">
/// <item><b>第一阶段（校验，零副作用）</b>：重名（批内／与现存实体——报重名、不加后缀）、
/// 源可解析（**两级**：资产声明表 → <see cref="PrefabCatalog"/> 的目录类型——裁决 12）、附件 <c>save</c>
/// 存在且服务可解析、附件类型与 <c>requires-host</c> 可满足、<c>Entity</c> 型私有字段的 name 可重解析（且唯一）。
/// **全部通过才进入第二阶段；失败 ⇒ 零实体、无痕迹**（回滚不是无声的——<c>Dispose</c> 发
/// <c>effect.removed</c>、<c>Assembled</c> 已发不可撤，能让校验发现的错误必须挡在零实体）。</item>
/// <item><b>第二阶段（构造）</b>：<see cref="PrefabExpander.Instantiate"/>（inline，复用沿链合并／饱和校验——零新展开逻辑）
/// 或**直接按目录类型建实体**（代码定义、无空位；裁决 12）→ <see cref="OrchestrationRuntime.CreateEntity"/>
/// （名字取 <c>savedEntities[].name</c>）→ 按 <c>attachments</c>
/// 顺序建出附件实例（**不查重构造＋不装配**，D58）→ **一次性写回全部私有字段**（根＋附件；批内入口，
/// 装配之前——装配期建边／<c>Assembled</c>／active-when 必须读到存档值，D55）→ 统一装配 → 运行期孤儿校验。
/// **失败 ⇒ 逆序销毁已建实体 ＋ 抛异常带残留清单**（销毁会发信号＝已知代价，引擎无事务）。</item>
/// </list>
/// <para>
/// slots 与 privateFields 的同名冲突 <b>privateFields 赢</b>（D55）：slots 经 <c>args</c> 注入初值
/// （展开期注入，S35——先），privateFields 在写回时覆盖（后）。
/// 附件实例名不在存档里（<see cref="SaveAttachment"/> 不带 name——份数与身份靠数组顺序，D54），
/// 恢复时按 <c>{宿主名}/{templateId}@{序}</c> 确定性重生成（宿主名在本运行时唯一 ⇒ 批量恢复不撞名；
/// 具体字符串属已知限制）。
/// </para>
/// </summary>
public sealed partial class OrchestrationRuntime
{
    /// <summary>
    /// 恢复一批可存档实体（读档；同一 moding 多次调用＝批量实例化——调用方给名）。
    /// </summary>
    /// <param name="savedEntities">存档条目（<c>savedEntities</c> 段；顺序即恢复序）。</param>
    /// <param name="declarations">
    /// 资产声明表（<c>extends</c> 链上的 <see cref="ComponentAsset"/>；与
    /// <see cref="PrefabExpander.Instantiate"/> 的 <c>Declarations</c> 同一输入面——
    /// 目录只存展开后的 <see cref="PrefabDefinition"/>，重展开需要资产形态）。
    /// <b>可以只给空表</b>：代码定义的预制体（Card／Unit 等）走目录类型路径，不需要资产条目（裁决 12）。
    /// </param>
    /// <param name="judicators">判定器注册表（可空注入；附件的 <c>save</c> 服务可解析性预检用——恢复出的实体必须可再存档）。</param>
    /// <returns>本次恢复创建的根实体（存档顺序）。</returns>
    /// <exception cref="InvalidOperationException">
    /// 第一阶段任一校验失败（零实体、无痕迹），或第二阶段构造失败（已逆序销毁、异常带残留清单），
    /// 或运行期孤儿校验未通过（有宿主却不在任何附件集——直建宿主实体形态，显式暴露）。
    /// </exception>
    public IReadOnlyList<OrchestrationEntity> Restore(
        IReadOnlyList<SavedEntityRecord> savedEntities,
        IReadOnlyDictionary<string, ComponentAsset> declarations,
        JudicatorRegistry? judicators)
    {
        ArgumentNullException.ThrowIfNull(savedEntities);
        ArgumentNullException.ThrowIfNull(declarations);

        if (savedEntities.Count == 0)
        {
            return Array.Empty<OrchestrationEntity>();
        }

        // ---------- 第一阶段：校验（零副作用——产出即恢复计划，第二阶段只消费它） ----------
        var plan = ValidateRestorePlan(savedEntities, declarations, judicators);

        // ---------- 第二阶段：构造（失败 ⇒ 逆序销毁 ＋ 异常带残留清单） ----------
        var created = new List<OrchestrationEntity>(plan.Count);
        try
        {
            // ① 建根实体（全部建完再写回：跨根的 entity 引用才可解析）。
            //    有资产形态 ⇒ Instantiate 重新展开（沿链合并／饱和校验）；只有目录类型 ⇒ 直接用它建实体
            //    （代码定义、结构固定、无空位——裁决 12：重展开没有意义）。
            foreach (var entry in plan)
            {
                created.Add(CreateEntity(
                    RootPrefabId(entry, declarations), entry.Record.Name));
            }

            // ② 挂附件实例（**不查重构造＋不装配**——D58；状态写回统一放到下一步）。
            //    先建齐再写回：解析表要覆盖全部实体，否则「引用本批某个附件实例」会在写回时找不到。
            var pending = new List<(ComponentAttachment Attachment, SaveAttachment Moding)>();
            for (var i = 0; i < plan.Count; i++)
            {
                var host = created[i];
                foreach (var attachment in plan[i].Attachments)
                {
                    var component = ConstructAttachmentForRestore(
                        host,
                        AttachmentPrefabId(attachment, declarations),
                        attachment.InstanceName);
                    pending.Add((component, attachment.Moding));
                }
            }

            // ③ 写回（根 ＋ 附件）：解析表在此刻已覆盖全部实体（跨根／跨附件引用都可解析）。
            var resolver = BuildNameResolver();
            for (var i = 0; i < plan.Count; i++)
            {
                WriteBack(created[i], plan[i].Record.Moding.PrivateFields, resolver);
            }

            foreach (var (attachment, moding) in pending)
            {
                WriteBack(attachment.Component, moding.PrivateFields, resolver);
            }

            // ④ 延后装配（写回已完成 ⇒ 建边／Assembled／active-when 读到存档值——D55 写回时机）。
            foreach (var host in created)
            {
                AssembleComponents(host, ActiveWhen.Loaded);
                AssembleComponents(host, ActiveWhen.Placed);
            }

            // ⑤ 运行期孤儿校验（有宿主却不在任何宿主附件集 ⇒ 抛；覆盖「直建 host 实体」形态）。
            EnsureNoOrphans();

            return created;
        }
        catch (Exception error)
        {
            var rolledBack = new List<string>(created.Count);
            for (var i = created.Count - 1; i >= 0; i--)
            {
                rolledBack.Add(created[i].Name);
                created[i].Destroy(); // 卸组件（发 effect.removed——若已接线）＋撤边＋Forget：已知代价（D57）
            }

            throw new InvalidOperationException(
                "恢复失败（第二阶段）：" + error.Message
                + "——已逆序销毁本次恢复创建的全部实体"
                + (rolledBack.Count > 0 ? $"（残留清单：{string.Join("、", rolledBack)}）" : "（尚未建出任何实体）")
                + "。销毁会发 effect.removed 等信号——引擎无事务，这是已知代价（D57），不是可撤销的静默回滚。",
                error);
        }
    }

    // ---------- 第一阶段 ----------

    /// <summary>
    /// 恢复计划校验（零副作用：只读目录、声明链、注册表与名字多重集）并**产出计划**——
    /// 第二阶段只消费这里解析好的源，不再重复解析／校验。
    /// </summary>
    private IReadOnlyList<RestoreEntry> ValidateRestorePlan(
        IReadOnlyList<SavedEntityRecord> savedEntities,
        IReadOnlyDictionary<string, ComponentAsset> declarations,
        JudicatorRegistry? judicators)
    {
        // ① 重名（裁决 9：报重名、不自动加后缀——名字是玩家与日志里唯一的识别依据）。
        //    名字**先全量登记**（根＋附件实例名），再进入②校验：否则「A 引用 B 的附件名」这类跨条目
        //    引用会随条目顺序时有时无（校验必须与恢复顺序解耦）。
        var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in Entities)
        {
            nameCounts[entity.Name] = nameCounts.TryGetValue(entity.Name, out var count) ? count + 1 : 1;
        }

        var batchNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in savedEntities)
        {
            if (!batchNames.Add(record.Name))
            {
                throw new InvalidOperationException(
                    $"恢复失败：存档条目重名 '{record.Name}'（同名恢复即失败，不自动加后缀——裁决 9）。");
            }

            if (nameCounts.ContainsKey(record.Name))
            {
                throw new InvalidOperationException(
                    $"恢复失败：'{record.Name}' 与场上现存实体重名（同名恢复即失败，不自动加后缀——裁决 9）。");
            }

            nameCounts[record.Name] = 1;

            for (var order = 0; order < record.Moding.Attachments.Count; order++)
            {
                var attachment = record.Moding.Attachments[order];
                var attachmentName = AttachmentInstanceName(record.Name, attachment.TemplateId, order);
                if (nameCounts.ContainsKey(attachmentName))
                {
                    throw new InvalidOperationException(
                        $"恢复失败：附件实例名 '{attachmentName}' 与场上或本批已有名字冲突"
                        + "（同名恢复即失败、不自动加后缀——裁决 9）。");
                }

                nameCounts[attachmentName] = 1;
            }
        }

        bool Resolvable(string name) => nameCounts.TryGetValue(name, out var count) && count == 1;

        // ② 逐条：源可解析（资产表 → 目录）＋ 附件规则 ＋ entity 型字段可重解析。
        var plan = new List<RestoreEntry>(savedEntities.Count);
        foreach (var record in savedEntities)
        {
            var moding = record.Moding;
            var rootSubject = $"实体 '{record.Name}'";
            var source = ResolveRestoreSource(moding.Extends, moding.Slots, declarations, rootSubject);

            // 根不能是引擎实体（对局级单例由运行时自动建；裁决 8 已把它排除在存档之外）。
            if (source.Type.Kind == EntityKind.Engine)
            {
                throw new InvalidOperationException(
                    $"恢复失败：{rootSubject} 的源预制体 '{moding.Extends}' 是引擎实体（对局级单例）——"
                    + "引擎实体由运行时自动建，不进 savedEntities（裁决 8）。");
            }

            ValidateEntityReferences(
                source.Type, moding.PrivateFields, rootSubject, Resolvable);

            var attachments = new List<RestoreAttachment>(moding.Attachments.Count);
            for (var order = 0; order < moding.Attachments.Count; order++)
            {
                var attachment = moding.Attachments[order];
                var subject = $"实体 '{record.Name}' 的附件 '{attachment.TemplateId}'（第 {order + 1} 份）";
                var attSource = ResolveRestoreSource(
                    attachment.TemplateId, attachment.Slots, declarations, subject);

                // 附件类型（D58／S6「附件类型可满足」）：只有组件能挂宿主，深度恒为 1。
                if (attSource.Type.Kind != EntityKind.Component)
                {
                    throw new InvalidOperationException(
                        $"恢复失败：{subject} 的源预制体 '{attachment.TemplateId}' 不是组件实体"
                        + $"（kind={attSource.Type.Kind}）——不能作为附件挂到 '{moding.Extends}'，挡在零实体（D57）。");
                }

                // 附件 save 门户＋服务可解析（恢复出的实体必须可再存档——Q2 门户语义在附件上的投影）。
                if (attSource.Type.Save is null)
                {
                    throw new InvalidOperationException(
                        $"恢复失败：{subject} 的源预制体未声明 save ⇒ 恢复出的实体不可再存档（拒绝）。");
                }

                if (judicators is null)
                {
                    throw new InvalidOperationException(
                        $"恢复失败：本对局未装配判定器注册表（Judicators == null）——{subject} 的 save 服务 "
                        + $"'{attSource.Type.Save}' 无法解析。");
                }

                try
                {
                    _ = judicators.Resolve(attSource.Type.Save);
                }
                catch (KeyNotFoundException error)
                {
                    throw new InvalidOperationException(
                        $"恢复失败：{subject} 的 save 服务 '{attSource.Type.Save}' 未注册（不自动兜底——D50）。", error);
                }

                // 附件类型与 requires-host 可满足（实际宿主＝恢复出的根；取其源的 Hosts 判定）。
                if (!attSource.Type.RequiresHost.HasFlag(source.Type.Hosts))
                {
                    throw new InvalidOperationException(
                        $"恢复失败：{subject} 的 requires-host={attSource.Type.RequiresHost} 不覆盖宿主类型 "
                        + $"'{moding.Extends}'（Hosts={source.Type.Hosts}）——装配必然失败，挡在零实体（D57）。");
                }

                ValidateEntityReferences(
                    attSource.Type, attachment.PrivateFields, subject, Resolvable);

                attachments.Add(new RestoreAttachment(
                    attachment,
                    attSource.Template,
                    AttachmentInstanceName(record.Name, attachment.TemplateId, order)));
            }

            plan.Add(new RestoreEntry(record, source.Template, attachments));
        }

        return plan;
    }

    /// <summary>
    /// <c>extends</c> 的**两级解析**（裁决 12）：①先查资产声明表（有资产形态 ⇒ 可 `Instantiate` 重新展开）；
    /// ②查不到再查 <see cref="PrefabCatalog"/> 的 <see cref="PrefabType"/>（代码定义、结构固定、**没有空位** ⇒
    /// 直接用类型建实体）。两处都没有 ⇒ 明确失败；只在目录里却带着 <c>slots</c> ⇒ 也失败（参数无处可注，
    /// 不静默丢弃）。
    /// </summary>
    /// <param name="slots">该源的 <c>slots</c>（只有目录类型时必须是空表）。</param>
    private RestoreSource ResolveRestoreSource(
        string id,
        IReadOnlyDictionary<string, object?> slots,
        IReadOnlyDictionary<string, ComponentAsset> declarations,
        string subject)
    {
        if (declarations.TryGetValue(id, out var template))
        {
            WalkChain(id, declarations, subject);
            return new RestoreSource(ResolveCatalogPrefab(id, subject), template);
        }

        PrefabType? type = null;
        try
        {
            type = Catalog.Resolve(id);
        }
        catch (KeyNotFoundException)
        {
            // 落到下面的「两处都没有」——文案要指出两个查法都试过（否则读者会以为是资产表漏了）。
        }

        if (type is null)
        {
            throw new InvalidOperationException(
                $"恢复失败：{subject} 的源 '{id}' 既不在资产声明表里、也不在预制体目录里——"
                + "恢复必须由 moding＋已装载资产世界可解析（D51 必要依赖；裁决 12 允许恢复代码定义的目录预制体，"
                + "但两处都没有就无从重建）。");
        }

        if (slots.Count > 0)
        {
            throw new InvalidOperationException(
                $"恢复失败：{subject} 的源 '{id}' 只能按目录预制体恢复（代码定义、无资产形态、无空位），"
                + $"但 moding 携带了 {slots.Count} 个 slots（{string.Join("、", slots.Keys)}）——"
                + "参数无处可注，拒绝静默丢弃（裁决 12）。");
        }

        return new RestoreSource(type, Template: null);
    }

    // ---------- 恢复计划（第一阶段产出、第二阶段消费） ----------

    /// <summary>恢复计划的一条：存档条目 ＋ 已解析的根源（<c>Template == null</c>＝目录预制体）＋ 附件。</summary>
    private sealed record RestoreEntry(
        SavedEntityRecord Record, ComponentAsset? Template, IReadOnlyList<RestoreAttachment> Attachments);

    /// <summary>恢复计划的一条附件：存档 moding ＋ 已解析的源（<c>Template == null</c>＝目录预制体）＋ 确定性实例名。</summary>
    private sealed record RestoreAttachment(SaveAttachment Moding, ComponentAsset? Template, string InstanceName);

    /// <summary>源解析结果：<see cref="Type"/> 恒有；<see cref="Template"/> 非 null ＝有资产形态（走展开）。</summary>
    private sealed record RestoreSource(PrefabType Type, ComponentAsset? Template);

    /// <summary>沿资产 <c>extends</c> 链逐级检查声明在位（环＝拒绝）。</summary>
    private static void WalkChain(
        string id, IReadOnlyDictionary<string, ComponentAsset> declarations, string subject)
    {
        var current = id;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!declarations.TryGetValue(current, out var asset))
            {
                throw new InvalidOperationException(
                    $"恢复失败：{subject} 的源声明 '{current}' 不在声明表里——恢复必须由 moding＋已装载资产世界"
                    + "可解析（D51：必要依赖，没有源无法展开；这不是「origin 变成依赖」）。");
            }

            if (!seen.Add(current))
            {
                throw new InvalidOperationException($"恢复失败：{subject} 的声明链含继承环（'{current}'）。");
            }

            if (asset.Extends is null)
            {
                return;
            }

            current = asset.Extends;
        }
    }

    /// <summary>目录解析（包一层明确文案）。</summary>
    private PrefabType ResolveCatalogPrefab(string prefabId, string subject)
    {
        try
        {
            return Catalog.Resolve(prefabId);
        }
        catch (KeyNotFoundException error)
        {
            throw new InvalidOperationException(
                $"恢复失败：{subject} 预制体 '{prefabId}' 不在目录里（必要依赖——没有源无法展开，D51）。", error);
        }
    }

    /// <summary>
    /// entity／entity[] 型字段的预检：存档值（名字）必须可重解析且唯一
    /// （解析域＝现存实体 ∪ 本批条目；同名歧义即失败——不静默置空）。
    /// </summary>
    private static void ValidateEntityReferences(
        PrefabType type,
        IReadOnlyDictionary<string, object?> fields,
        string subject,
        Func<string, bool> resolvable)
    {
        foreach (var field in type.PrivateFields)
        {
            if (field.Type is not (ViewFieldType.Entity or ViewFieldType.EntityArray)
                || !fields.TryGetValue(field.Name, out var value)
                || value is null)
            {
                continue;
            }

            switch (value)
            {
                case string name:
                    CheckReference(name, field.Name, subject, resolvable);
                    break;
                case Array array:
                    foreach (var item in array)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        CheckReference(
                            item as string ?? throw new InvalidOperationException(
                                $"恢复失败：{subject} 的私有字段 '{field.Name}'（entity[]）含非名字元素"
                                + $"（{item.GetType().Name}）——存档形态应为实体名数组。"),
                            field.Name, subject, resolvable);
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"恢复失败：{subject} 的私有字段 '{field.Name}'（entity 型）的存档值形态不符"
                        + $"（{value.GetType().Name}，应为实体名或 null）。");
            }
        }
    }

    private static void CheckReference(
        string name, string fieldName, string subject, Func<string, bool> resolvable)
    {
        if (!resolvable(name))
        {
            throw new InvalidOperationException(
                $"恢复失败：{subject} 的私有字段 '{fieldName}' 引用的实体名 '{name}' 不存在或有歧义"
                + "（同名恢复即失败、不静默置空——裁决 9）。");
        }
    }

    // ---------- 第二阶段 ----------

    /// <summary>名字 → 实体解析表（现存实体 ∪ 本次恢复建出的全部实体；引用唯一性已由第一阶段保证）。</summary>
    private IReadOnlyDictionary<string, OrchestrationEntity> BuildNameResolver()
    {
        var resolver = new Dictionary<string, OrchestrationEntity>(StringComparer.Ordinal);
        foreach (var entity in Entities)
        {
            resolver[entity.Name] = entity; // 同名歧义已被第一阶段拒绝；后写覆盖只是防御
        }

        return resolver;
    }

    /// <summary>写回一份私有字段表（entity 名字先解析成实体；再走批内入口——类型校验照做）。</summary>
    private void WriteBack(
        OrchestrationEntity entity,
        IReadOnlyDictionary<string, object?> fields,
        IReadOnlyDictionary<string, OrchestrationEntity> resolver)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in fields)
        {
            var field = entity.Type.FindPrivateField(pair.Key)
                ?? throw new InvalidOperationException(
                    $"恢复失败：实体 '{entity.Name}' 的存档含未声明私有字段 '{pair.Key}'（结构已漂移——拒绝）。");

            values[pair.Key] = pair.Value switch
            {
                null => null,
                _ when field.Type == ViewFieldType.Entity => ResolveReference(pair.Value, pair.Key, entity, resolver),
                _ when field.Type == ViewFieldType.EntityArray => ResolveReferenceArray(
                    pair.Value, pair.Key, entity, resolver),
                _ => pair.Value,
            };
        }

        entity.RestorePrivateFields(values);
    }

    private static OrchestrationEntity ResolveReference(
        object value, string fieldName, OrchestrationEntity entity,
        IReadOnlyDictionary<string, OrchestrationEntity> resolver)
        => value is string name
            ? (resolver.TryGetValue(name, out var referenced)
                ? referenced
                : throw new InvalidOperationException(
                    $"恢复失败：实体 '{entity.Name}' 的私有字段 '{fieldName}' 引用的实体名 '{name}' 不存在。"))
            : throw new InvalidOperationException(
                $"恢复失败：实体 '{entity.Name}' 的私有字段 '{fieldName}'（entity 型）的存档值形态不符"
                + $"（{value.GetType().Name}，应为实体名）。");

    private static OrchestrationEntity?[] ResolveReferenceArray(
        object value, string fieldName, OrchestrationEntity entity,
        IReadOnlyDictionary<string, OrchestrationEntity> resolver)
        => value is Array array
            ? array.Cast<object?>()
                .Select(item => item is null ? null : ResolveReference(item, fieldName, entity, resolver))
                .ToArray()
            : throw new InvalidOperationException(
                $"恢复失败：实体 '{entity.Name}' 的私有字段 '{fieldName}'（entity[] 型）的存档值形态不符"
                + $"（{value.GetType().Name}，应为实体名数组）。");

    /// <summary>
    /// 根的建实体键：有资产形态 ⇒ 展开产物的 inline 缓存键；只有目录类型 ⇒ 预制体 id 本身（裁决 12：
    /// 代码定义的预制体结构固定、没有空位，重展开没有意义）。
    /// </summary>
    private string RootPrefabId(RestoreEntry entry, IReadOnlyDictionary<string, ComponentAsset> declarations)
        => entry.Template is { } template
            ? InstantiateForRestore(template, entry.Record.Moding.Slots, declarations).CacheKey!
            : entry.Record.Moding.Extends;

    /// <summary>附件的建实体键（判据同 <see cref="RootPrefabId"/>）。</summary>
    private string AttachmentPrefabId(
        RestoreAttachment attachment, IReadOnlyDictionary<string, ComponentAsset> declarations)
        => attachment.Template is { } template
            ? InstantiateForRestore(template, attachment.Moding.Slots, declarations).CacheKey!
            : attachment.Moding.TemplateId;

    /// <summary>值表面展开（唯一展开入口：沿链合并／饱和校验全复用——零新展开逻辑）。</summary>
    private InstantiatedPrefab InstantiateForRestore(
        ComponentAsset template,
        IReadOnlyDictionary<string, object?> slots,
        IReadOnlyDictionary<string, ComponentAsset> declarations)
        => PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = slots,
            Mode = ExpansionMode.Inline,
            Declarations = declarations,
            InlineTable = InlinePrefabs,
        });

    /// <summary>
    /// 附件实例名（确定性重生成）：<c>{宿主名}/{templateId}@{序}</c>。
    /// 存档不带附件名（份数与身份靠数组顺序），而宿主名在本运行时唯一（第一阶段已拒绝重名）⇒
    /// 同名宿主不可能出现，批量恢复（同一 moding 多次）也不会撞名。
    /// </summary>
    private static string AttachmentInstanceName(string hostName, string templateId, int order)
        => $"{hostName}/{templateId}@{order + 1}";

    /// <summary>D58 恢复附件入口：不查重构造＋不装配（装配由恢复流程在写回后统一驱动）。</summary>
    private ComponentAttachment ConstructAttachmentForRestore(OrchestrationEntity host, string prefabId, string name)
    {
        var type = ResolveEntityType(prefabId);
        if (type.Kind != EntityKind.Component)
        {
            throw new InvalidOperationException(
                $"恢复失败：'{prefabId}' 不是组件实体（kind={type.Kind}），不能作为附件挂到 '{host.Name}'。");
        }

        if (!_componentSets.TryGetValue(host, out var set))
        {
            set = new ComponentAttachmentSet(host);
            _componentSets[host] = set;
        }

        return set.ConstructUnchecked(this, prefabId, name);
    }

    /// <summary>
    /// 运行期孤儿校验（S6／第二批收口）：**任何**有宿主的实体必须在某宿主的附件集里——
    /// 否则它的状态既不进 <c>savedEntities</c>（有宿主被排除）也不进任何 attachments（不在装配面）＝
    /// 存档静默丢失。覆盖「直接 <c>CreateEntity(id, name, host)</c> 绕过组件装配面」的遗留形态：
    /// 存档侧（<see cref="SaveEvaluation"/>）与恢复侧（本方法）双重守卫，显式失败、不静默。
    /// </summary>
    private void EnsureNoOrphans()
    {
        var attached = new HashSet<OrchestrationEntity>(
            Components.Select(static attachment => attachment.Component));
        var orphans = Entities
            .Where(entity => entity.Host is not null && !attached.Contains(entity))
            .ToList();
        if (orphans.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "恢复失败：运行期孤儿校验未通过——以下实体有宿主却不在任何宿主的附件集里"
            + "（其状态不进任何存档、恢复也无法重建，必须显式暴露）："
            + string.Join("、", orphans.Select(static o => $"'{o.Name}'(预制体 '{o.Type.Id}'，宿主 '{o.Host!.Name}')"))
            + "。成因＝直接 CreateEntity(id, name, host) 绕过组件装配面；请改走 ConstructComponent／GrantComponent。");
    }
}
