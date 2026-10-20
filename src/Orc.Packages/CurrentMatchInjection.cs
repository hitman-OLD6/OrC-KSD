using Orc.Core;

namespace Orc.Packages;

/// <summary>
/// 当前对局注入面（S9；由**游戏层**实现——复用既有 <c>EffectRuntime</c> 动态装载链）。
/// 卡包热加载默认只对新建对局生效；把某效果注入**当前**对局属**显式受控动作**，经本面转接。
/// </summary>
public interface ICurrentMatchInjector
{
    /// <summary>把某效果预制体注入当前对局（成功＝true）。</summary>
    Task<bool> InjectEffectPrefabAsync(string prefabId, CancellationToken ct = default);
}

/// <summary>
/// 卡包 → 当前对局的显式受控注入（Q4=c 两级之第二级）：先校验目标卡包在本局快照中（版本锁定一致），
/// 再逐条经 <see cref="ICurrentMatchInjector"/> 注入；返回失败明细（空＝全部成功）。
/// </summary>
public static class CardPackInjection
{
    /// <summary>把卡包声明的效果注入当前对局。</summary>
    /// <exception cref="ArgumentNullException">injector／snapshot／packId／effectPrefabIds 为 null。</exception>
    public static async Task<IReadOnlyList<string>> InjectEffectsAsync(
        ICurrentMatchInjector injector,
        AssetCatalogSnapshot snapshot,
        string packId,
        PackageVersion version,
        IReadOnlyList<string> effectPrefabIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(effectPrefabIds);

        var failures = new List<string>();
        if (snapshot.Find(packId, version) is null)
        {
            failures.Add($"卡包 '{packId}@{version}' 不在本局快照中（版本锁定不一致——拒绝注入）。");
            return failures;
        }

        foreach (var prefabId in effectPrefabIds)
        {
            if (string.IsNullOrWhiteSpace(prefabId))
            {
                failures.Add("效果预制体 id 为空白。");
                continue;
            }

            if (!await injector.InjectEffectPrefabAsync(prefabId, ct).ConfigureAwait(false))
            {
                failures.Add($"效果预制体 '{prefabId}' 注入失败。");
            }
        }

        return failures;
    }
}
