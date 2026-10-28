using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Migration;

namespace Orc.Game.Cards;

/// <summary>
/// **旧效果快照 → v4 组件资产**的桥（v4-only 后的过渡件，删 <c>Effect</c> 家族的最后一块前置）。
/// <para>
/// 背景：卡面文本编译器（<c>EffectCompiler</c>）与内联效果注册面产出的仍是旧 <see cref="EffectSnapshot"/>
/// （<c>mainTrigger</c> ＋ <c>otherTriggers</c> ＋ <c>wrappers</c> ＋ <c>injects</c>），
/// 而 v4 的执行面要的是「成员 ＋ 挂载 ＋ 包装」。本类把前者无损转成后者：
/// </para>
/// <list type="number">
/// <item><see cref="PrefabJson.SerializeForDisk"/> 把快照写回**旧格式 JSON 文本**（已有契约出口，不新增序列化逻辑）；</item>
/// <item><see cref="AssetSchemaMigrator.Migrate"/> 把它转成 v4 组件 JSON——
///   其中 <c>viewType</c>→字段清单的映射复用迁移器的 <c>LegacyViewCatalog</c>（同一张表，不另立真源）；</item>
/// <item><see cref="AssetSchema.Read"/> 读成 <see cref="ComponentAsset"/>，按 <see cref="AssetPackLoader"/> 同款顺序
///   **装载期预编译 csx → 注册 handler → 注册预制体目录**（S5 第 9 条：不保留懒编译）。</item>
/// </list>
/// <para>
/// 为什么值得这样做：转换后卡走**同一条 v4 装配路径**（<c>CardBase.LoadModelComponents</c> 与
/// <c>OrchestrationRuntime.AddComponent</c>），旧 <c>Effect</c> 实例链在卡装载面不再被触碰——
/// 这是删 <c>Effect</c>／<c>DynamicEffect</c> ／<c>CardEffectLoader</c> 的前提。
/// </para>
/// <para>
/// 失败面：结构不受支持（迁移器抛）／csx 编译失败／判定器包装求值失败＝**抛出**（fail-fast）。
/// 调用方按"单条隔离"处理还是"整卡失败"处理，由调用方决定（卡装载面沿用隔离口径）。
/// </para>
/// </summary>
internal static class LegacySnapshotComponentBinder
{
    /// <summary>
    /// 把一份旧效果快照转成 v4 组件并注册进编排运行时；返回组件 id。
    /// 已注册（同 id 全局一份）＝直接返回，不重复编译。
    /// </summary>
    /// <param name="runtime">编排运行时（目的一：预制体目录 ＋ handler 表）。</param>
    /// <param name="snapshot">旧效果快照。</param>
    /// <param name="evaluator">异步脚本求值器（csx 必须；null＝该资产含 csx，直接失败）。</param>
    /// <param name="judgeEvaluator">同步脚本求值器（判定器包装必须；无包装时可为 null）。</param>
    /// <exception cref="ArgumentNullException">runtime／snapshot 为 null。</exception>
    /// <exception cref="InvalidOperationException">快照结构不受支持，或缺求值器／csx 编译失败。</exception>
    internal static string Register(
        OrchestrationRuntime runtime,
        EffectSnapshot snapshot,
        IScriptEvaluator? evaluator,
        ISyncScriptEvaluator? judgeEvaluator)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(snapshot);

        // 前置：assemblyKey 成员（C# 委托来源）不在 v4 覆盖面内。**必须在迁移之前**判定——
        // 否则迁移器对旧结构的拒绝会掩盖"应回退旧链"这个信号，表现成"该效果被当成装配失败而隔离"
        // （纯 C# 委托效果整类失效，且日志指向迁移器、误导排查方向）。
        foreach (var trigger in EnumerateTriggers(snapshot))
        {
            foreach (var ev in trigger.Events)
            {
                if (!string.IsNullOrWhiteSpace(ev.AssemblyKey))
                {
                    throw new LegacyAssemblyKeyNotSupportedException(
                        $"资产 '{snapshot.Root.Id}' 的触发器 '{trigger.Id}' 的事件 '{ev.Id}' 用 assembly-key"
                        + $"（'{ev.AssemblyKey}'）：v4 组件只接受可预编译的 csx。");
                }
            }
        }

        var asset = Convert(snapshot);


        if (runtime.Catalog.Contains(asset.Id))
        {
            return asset.Id; // 同 id 全局一份（重复装载不重复编译）
        }

        // ① 成员 handler：装载期预编译（编译失败＝抛出，不留半个可寻址成员）。
        foreach (var member in asset.Members)
        {
            if (member.Handler is not { } handler)
            {
                continue; // 无 handler（纯壳成员）：无需注册
            }

            if (handler.Script is null)
            {
                // 只有 assembly-key（C# 委托来源）——v4 面只接受可预编译的 csx（同 AssetPackLoader 口径）。
                // 抛专用异常让调用方**回退旧链**，而不是静默降级或隔离（旧链正是 assemblyKey 的承载面）。
                throw new LegacyAssemblyKeyNotSupportedException(
                    $"资产 '{asset.Id}' 的成员 '{member.Name}' 只有 assembly-key（'{handler.Id}'），"
                    + "没有 csx 源码：v4 组件只接受可预编译的 csx。");
            }

            if (evaluator is null)
            {
                throw new InvalidOperationException(
                    $"旧效果快照 '{asset.Id}' 的成员 '{member.Name}' 含 csx，但没有装配脚本求值器——"
                    + "装载期预编译无法进行（S5 第 9 条：不保留懒编译）。");
            }

            try
            {
                runtime.Handlers.RegisterScript(
                    handler.Id,
                    handler.Script,
                    handler.Entry,
                    evaluator,
                    member.View,
                    OwnerMembersOf(asset));
            }
            catch (PrefabLoadException ex) when (ex.Category == ScriptErrorCategories.Compile)
            {
                // csx 编译不兼容（最常见：`is not T v` 在 `||` 右操作数失去明确赋值——承载视图是 `dynamic`，
                // 旧强类型视图下这是合法写法）。这类资产属"旧来源"（文本编译器模板 / 沙盒原语 / 测试内联），
                // 抛专用异常让调用方**回退旧链**：旧链用强类型视图编译同一段 csx，能过就照旧生效。
                throw new LegacyScriptUnsupportedException(
                    $"资产 '{asset.Id}' 的成员 '{member.Name}' csx 在承载视图（dynamic）下编译失败：{ex.Message}", ex);
            }
        }

        // ② 判定器包装：同步求值并注册（与 AssetPackLoader 的判定器包装同款）。
        foreach (var judgeWrap in asset.JudgeWraps)
        {
            var id = ComponentAsset.JudgeHandlerId(asset.Id, judgeWrap.Judicator);
            if (runtime.Handlers.TryResolve(id, out _))
            {
                continue;
            }

            if (judgeEvaluator is null)
            {
                throw new InvalidOperationException(
                    $"旧效果快照 '{asset.Id}' 的判定器包装 '{judgeWrap.Judicator}' 含 csx，"
                    + "但没有装配同步脚本求值器——装载期预编译无法进行。");
            }

            var result = judgeEvaluator.Evaluate(new SyncScriptRequest(judgeWrap.Script, judgeWrap.Entry));
            if (!result.Success || result.Handler is null)
            {
                throw new InvalidOperationException(
                    $"旧效果快照 '{asset.Id}' 的判定器包装 '{judgeWrap.Judicator}' 装载期校验失败："
                    + $"[{result.ErrorCategory}] {result.Error}");
            }

            runtime.Handlers.RegisterJudge(id, result.Handler);
        }

        // ③ 预制体展开（继承/可见性/字段撞名/one-shot 无触发点等在此一次性暴露）→ 注册进目录。
        runtime.Catalog.Add(asset.ToPrefabDefinition(runtime.Handlers.Resolve));
        return asset.Id;
    }

    /// <summary>
    /// 转换结果缓存：键＝旧快照序列化文本的哈希（同一 id 不同内容也不串味），值＝v4 组件资产。
    /// <para>
    /// 为什么值得缓存：迁移器是**文本处理**（<c>JsonDocument</c> ＋ 大量字符串与正则级操作），
    /// 而卡装载面在每局都会对同一批快照重复触发（同一 prefab 被多张卡引用、多个对局各建一次运行时）。
    /// <see cref="ComponentAsset"/> 不可变，跨运行时复用安全；handler 注册与预制体展开仍按运行时各做一次。
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, ComponentAsset> AssetCache = new(StringComparer.Ordinal);

    /// <summary>旧快照 → v4 组件资产（带缓存）。</summary>
    private static ComponentAsset Convert(EffectSnapshot snapshot)
    {
        var legacyJson = PrefabJson.SerializeForDisk(snapshot);
        var key = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyJson)), 0, 16);
        if (AssetCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var migrated = AssetSchemaMigrator.Migrate(legacyJson, snapshot.Root.Id);
        var asset = AssetSchema.Read(migrated.Json, migrated.AssetId);
        AssetCache[key] = asset;
        return asset;
    }

    /// <summary>快照里参与装载的触发器集合（主 ＋ 其它 ＋ 注销兜底）。</summary>
    private static IEnumerable<TriggerPrefab> EnumerateTriggers(EffectSnapshot snapshot)
    {
        yield return snapshot.Root.MainTrigger;
        foreach (var other in snapshot.Root.OtherTriggers)
        {
            yield return other;
        }

        if (snapshot.Root.DisposingTrigger is { } disposing)
        {
            yield return disposing;
        }
    }

    /// <summary>
    /// 该资产的 csx 在承载视图（<c>dynamic</c>）下无法编译——调用方应回退旧链（强类型视图可编）。
    /// 典型原因：<c>is not T v</c> 出现在 <c>||</c> 左操作数（dynamic 下失去明确赋值分析）。
    /// </summary>
    internal sealed class LegacyScriptUnsupportedException : Exception
    {
        /// <summary>创建异常。</summary>
        public LegacyScriptUnsupportedException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// 该资产含 <c>assemblyKey</c> 形式的成员（C# 委托来源）——v4 面不承载，调用方应回退旧链。
    /// </summary>
    internal sealed class LegacyAssemblyKeyNotSupportedException : Exception
    {
        /// <summary>创建异常。</summary>
        public LegacyAssemblyKeyNotSupportedException(string message) : base(message)
        {
        }
    }

    /// <summary>该资产的 <c>owner</c> 通道允许名字（私有字段 ∪ 实体公开属性；S5 第 6d 条）。</summary>
    private static IReadOnlyCollection<string> OwnerMembersOf(ComponentAsset asset)
    {
        var names = new HashSet<string>(PrefabType.BuiltInOwnerMembers, StringComparer.Ordinal);
        foreach (var field in asset.PrivateFields)
        {
            names.Add(field.Name);
        }

        return names;
    }
}
