using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;

namespace Orc.Packages;

/// <summary>
/// 卡包内容 → 对局注册面（装配序：先把效果声明/预制体/效果库装进对局注册面，再由 <c>Match.Initialize</c> 装载卡）。
/// **须在 <c>Match.Initialize</c> 之前调用**（注册先于卡加载）。
/// <para>
/// **原子（S14 第 1 条）**：每个包"先全量校验（含 csx 预编译）再统一注册"——包内任一资产失败＝整包零注册。
/// **必需/可选（第 2 条）**：必需包失败＝抛出（对局不开局）；可选包失败＝跳过该包并在装载门报告留痕（第 3 条）。
/// </para>
/// <para>
/// **装载格式（U5-a／R1i）**：包提供 <c>effects-v4/</c>（组件资产）＝**只装 v4**——经 <see cref="AssetPackLoader"/>
/// 原子装进对局的**编排运行时**（<c>engine.Model</c>），旧格式目录（<c>effects/</c>，<c>*.prefab.json</c>）
/// **不再进入装载路径**；卡上的 <c>effects.prefabs</c> 由卡牌↔实体桥按声明序装配为组件实例。
/// 只提供旧格式目录的包＝过渡兼容路径（旧效果链），用于尚未迁移的卡包。
/// </para>
/// </summary>
public static class CardPackAssembler
{
    /// <summary>把若干卡包内容按给定序装配进对局的注册面。</summary>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="InvalidDataException">**必需**卡包的效果库装载失败（对局不开局）。</exception>
    public static void Attach(
        CardEffectRegistry registry, LogicEngine engine, IReadOnlyList<CardPackContent> packs)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(packs);

        foreach (var pack in packs)
        {
            var label = pack.Manifest.Id;
            var required = pack.Manifest.Required;
            var failures = new List<string>();
            AssetPackLoadResult? componentLoad = null;

            // ① 全量校验（只读）。
            if (pack.ComponentsDirectory is { } componentsDirectory)
            {
                // v4：解析 + csx 预编译 + 真实挂载门校验，全部落在**暂存面**（正式运行时不动）。
                componentLoad = AssetPackLoader.Load(
                    ResolveRuntime(engine),
                    componentsDirectory,
                    engine.ScriptEvaluator,
                    engine.SyncScriptEvaluator,
                    pack.Manifest.Id);
                failures.AddRange(componentLoad.Errors.Select(static e => $"{e.Target}: {e.Message}"));
            }
            else if (pack.EffectsDirectory is not null)
            {
                // 旧格式目录（effects/*.prefab.json）随旧装载链删除——不再装载，明确拒绝而不是静默无效。
                failures.Add("旧格式 effects/ 目录已不再装载（v4-only：请提供 effects-v4/*.component.json）。");
            }

            // 跨包 id 冲突也在注册前判出来（否则整包注册会在中途抛出、留下半包）。
            foreach (var snapshot in pack.InlinePrefabs)
            {
                if (engine.Prefabs.PrefabIds.Contains(snapshot.Root.Id, StringComparer.Ordinal))
                {
                    failures.Add($"效果预制体 '{snapshot.Root.Id}' 已在注册面上（跨包 id 冲突）。");
                }
            }

            if (failures.Count > 0)
            {
                engine.LoadGate.Record(
                    label,
                    required,
                    required ? PackLoadStatus.Failed : PackLoadStatus.Skipped,
                    failures);

                if (required)
                {
                    throw new InvalidDataException(
                        $"必需卡包 '{label}' 效果库装载失败（对局不开局，S14 第 2 条）："
                        + string.Join("；", failures));
                }

                continue; // 可选包：整包跳过（声明、内联、目录一律不注册）。
            }

            // ② 统一注册（此刻才触碰注册面）。
            // 卡 → 效果 id 的唯一声明面：v4 包里这些 id 在编排目录里直取，非 v4 包由快照现转组件。
            foreach (var (cardId, prefabIds) in pack.Declarations)
            {
                registry.DeclarePrefab(cardId, prefabIds);
            }

            foreach (var snapshot in pack.InlinePrefabs)
            {
                engine.Prefabs.RegisterPrefab(snapshot);
            }

            engine.LoadGate.Record(label, required, PackLoadStatus.Loaded);
        }
    }

    /// <summary>取对局编排运行时（未装配＝内核级最小运行时；与审查面同一回退口径）。</summary>
    private static OrchestrationRuntime ResolveRuntime(LogicEngine engine)
    {
        if (engine.Model is { } runtime)
        {
            return runtime;
        }

        var created = Orc.Game.Model.GameEnginePrefabs.CreateRuntime();
        engine.Model = created;
        return created;
    }
}
