namespace Orc.Game.Actions;

/// <summary>动作实现的来源（<see cref="ActionImplementationRegistry.Describe"/> 的取值）。</summary>
public enum ActionImplementationSource
{
    /// <summary>来自默认实现集（<see cref="ActionImplementationRegistry.SetDefault"/>）。</summary>
    Default = 0,

    /// <summary>来自单动作覆盖（<see cref="ActionImplementationRegistry.Override"/>）。</summary>
    Override = 1,

    /// <summary>默认集与覆盖集均未提供（装配期会 fail-fast）。</summary>
    Missing = 2,
}

/// <summary>
/// 动作实现注册面（R17 / Constraints 4 例外）：**进程级、单一真源、可枚举**。
/// <para>
/// 合并规则：单动作覆盖优先 → 默认实现集兜底 → 仍缺失即抛（错误信息含 <see cref="ActionId"/>）。
/// 线程安全（进程级锁）、幂等（重复注册替换同键、不会叠加）、可重置（<see cref="Reset"/> 仅供测试）。
/// </para>
/// <para>
/// 注册时点：宿主在装配动作目录之前调用一次 <c>SetDefault</c>（或插件的 <c>Register</c>）。
/// 未注册任何实现集时，<see cref="Resolve"/> 立即失败，不静默降级。
/// </para>
/// </summary>
public static class ActionImplementationRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<ActionId, ActionImplementationSet> Overrides = new();

    private static ActionImplementationSet? _default;

    /// <summary>注册/替换默认实现集（整包；后一次调用覆盖前一次）。</summary>
    /// <param name="set">默认实现集（允许只含部分动作——缺失项由 <see cref="Resolve"/> 在装配期报出）。</param>
    /// <exception cref="ArgumentNullException">set 为 null。</exception>
    public static void SetDefault(ActionImplementationSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (Sync)
        {
            _default = set;
        }
    }

    /// <summary>注册/替换单个动作的实现（只覆盖该 <see cref="ActionId"/>，其余仍用默认集）。</summary>
    /// <param name="id">被覆盖的动作 id。</param>
    /// <param name="set">覆盖集：**只能**包含该动作的工厂（其余须为 null）。</param>
    /// <exception cref="ArgumentNullException">set 为 null。</exception>
    /// <exception cref="InvalidOperationException">覆盖集缺少该动作工厂，或混入其他动作的工厂。</exception>
    public static void Override(ActionId id, ActionImplementationSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (Sync)
        {
            if (!set.HasImplementation(id))
            {
                throw new InvalidOperationException($"动作实现覆盖：集合中缺少动作 '{id}' 的工厂。");
            }

            if (set.ImplementedCount != 1)
            {
                throw new InvalidOperationException(
                    $"动作实现覆盖：集合只能包含动作 '{id}' 的工厂（实际提供 {set.ImplementedCount} 个）。");
            }

            Overrides[id] = set;
        }
    }

    /// <summary>
    /// 解析当前生效的完整实现集：逐动作「覆盖优先 → 默认兜底」，仍缺失即抛出（含缺失的 <see cref="ActionId"/>）。
    /// </summary>
    /// <exception cref="InvalidOperationException">未注册任何实现集，或存在缺失实现的动作。</exception>
    public static ActionImplementationSet Resolve()
    {
        lock (Sync)
        {
            if (_default is null && Overrides.Count == 0)
            {
                throw new InvalidOperationException(
                    "未注册动作实现：请先注册默认实现集（ActionImplementationRegistry.SetDefault / FoundationActions.Register）。");
            }

            var merged = _default ?? new ActionImplementationSet();
            foreach (var overlay in Overrides.Values)
            {
                merged = merged.Overlay(overlay);
            }

            var missing = ActionId.All.Where(id => !merged.HasImplementation(id)).Select(id => id.Value).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"未注册动作实现：动作 {string.Join("、", missing.Select(value => $"'{value}'"))} 缺少实现（default/override 均为空）。");
            }

            return merged;
        }
    }

    /// <summary>查询每个内置动作当前生效的实现来源（default / override / 缺失）。</summary>
    public static IReadOnlyDictionary<ActionId, ActionImplementationSource> Describe()
    {
        lock (Sync)
        {
            var sources = new Dictionary<ActionId, ActionImplementationSource>(ActionId.All.Count);
            foreach (var id in ActionId.All)
            {
                sources[id] = Overrides.ContainsKey(id)
                    ? ActionImplementationSource.Override
                    : _default?.HasImplementation(id) == true
                        ? ActionImplementationSource.Default
                        : ActionImplementationSource.Missing;
            }

            return sources;
        }
    }

    /// <summary>清空全部注册（仅供测试；生产装配不使用）。</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _default = null;
            Overrides.Clear();
        }
    }
}
