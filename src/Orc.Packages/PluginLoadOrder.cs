namespace Orc.Packages;

/// <summary>
/// 插件装载序声明（S4）：<paramref name="PackageId"/> 须排在其 <paramref name="LoadAfter"/> 中的包之后。
/// 纯包 ID、**代码内声明**（不进 manifest）——插件无 lock/sidecar，"资产拓扑"在其形态上无定义，
/// 故以显式装配声明落地。
/// </summary>
/// <param name="PackageId">本包 id。</param>
/// <param name="LoadAfter">须先于本包装载的包 id（纯包 ID）。</param>
public sealed record PackageLoadOrderEntry(string PackageId, IReadOnlyList<string> LoadAfter);

/// <summary>
/// 插件装载序解析与校验（S4）：三项启动断言——①显式序覆盖全部已发现插件；②无环；③无未知包 ID。
/// 任一不满足＝启动失败并指名（<see cref="Sort"/> 返回违规明细，宿主据此抛错）。
/// </summary>
public static class PluginLoadOrder
{
    /// <summary>
    /// 按显式声明排出插件装载序（稳定：同层按声明序），并返回违规明细（空＝通过）。
    /// </summary>
    /// <param name="declared">显式序声明（内置包注册清单）。</param>
    /// <param name="discoveredPluginIds">本次实际已发现的插件包 id。</param>
    /// <param name="violations">违规明细（①未覆盖／②环／③未知包 ID）。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    public static IReadOnlyList<string> Sort(
        IReadOnlyList<PackageLoadOrderEntry> declared,
        IReadOnlyCollection<string> discoveredPluginIds,
        out IReadOnlyList<string> violations)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(discoveredPluginIds);

        var problems = new List<string>();

        // 声明表本身：id 重复 = 无法判定，按未知处理
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declared.Count; i++)
        {
            if (!index.TryAdd(declared[i].PackageId, i))
            {
                problems.Add($"插件装载序声明中包 id '{declared[i].PackageId}' 重复。");
            }
        }

        // ③ 无未知包 ID：LoadAfter 目标必须在声明表内
        foreach (var entry in declared)
        {
            foreach (var target in entry.LoadAfter)
            {
                if (!index.ContainsKey(target))
                {
                    problems.Add(
                        $"插件装载序引用了未知包 ID '{target}'（声明方 '{entry.PackageId}'）。");
                }
            }
        }

        // ① 显式序覆盖全部已发现插件
        foreach (var id in discoveredPluginIds)
        {
            if (!index.ContainsKey(id))
            {
                problems.Add($"插件装载序未覆盖已发现插件 '{id}'（须在注册清单中显式声明）。");
            }
        }

        // ② 无环（在声明图上判定；环上的包必然无法排序）
        var cycles = FindCycles(declared, index);
        problems.AddRange(cycles);

        // 排序：仅已发现且在声明表内的插件；同层按声明序
        var discovered = new HashSet<string>(discoveredPluginIds, StringComparer.Ordinal);
        var sorted = new List<string>(discovered.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        while (sorted.Count < discovered.Count)
        {
            var advanced = false;
            for (var i = 0; i < declared.Count; i++)
            {
                var id = declared[i].PackageId;
                if (!discovered.Contains(id) || emitted.Contains(id))
                {
                    continue;
                }

                var ready = declared[i].LoadAfter.All(target =>
                    !discovered.Contains(target) || emitted.Contains(target));
                if (!ready)
                {
                    continue;
                }

                emitted.Add(id);
                sorted.Add(id);
                advanced = true;
            }

            if (!advanced)
            {
                break; // 环：剩余项无法排序（违规已在 cycles 中报告）
            }
        }

        // 未被排入的已发现插件（环导致）追加在末尾，保证不丢包
        foreach (var id in discoveredPluginIds)
        {
            if (emitted.Add(id))
            {
                sorted.Add(id);
            }
        }

        violations = problems.Distinct(StringComparer.Ordinal).ToArray();
        return sorted;
    }

    private static IEnumerable<string> FindCycles(
        IReadOnlyList<PackageLoadOrderEntry> declared, Dictionary<string, int> index)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0＝未访问 1＝在栈 2＝完成
        var path = new List<string>();

        foreach (var entry in declared)
        {
            foreach (var cycle in Visit(entry.PackageId))
            {
                yield return cycle;
            }
        }

        IEnumerable<string> Visit(string id)
        {
            if (!index.ContainsKey(id))
            {
                yield break;
            }

            if (state.TryGetValue(id, out var value))
            {
                if (value == 1)
                {
                    var start = path.IndexOf(id);
                    var loop = string.Join(" → ", path.Skip(start < 0 ? 0 : start).Append(id));
                    yield return $"插件装载序存在环：{loop}。";
                }

                yield break;
            }

            state[id] = 1;
            path.Add(id);
            foreach (var target in declared[index[id]].LoadAfter)
            {
                foreach (var cycle in Visit(target))
                {
                    yield return cycle;
                }
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
    }
}
