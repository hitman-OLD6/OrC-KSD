using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;

namespace Orc.Packages;

/// <summary>
/// 卡包内容 → 对局注册面（装配序：先把效果声明/预制体/效果库装进对局注册面，再由 <c>Match.Initialize</c> 装载卡）。
/// **须在 <c>Match.Initialize</c> 之前调用**（注册先于卡加载）。
/// </summary>
public static class CardPackAssembler
{
    /// <summary>把若干卡包内容按给定序装配进对局的注册面。</summary>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="InvalidDataException">效果库装载失败。</exception>
    public static void Attach(
        CardEffectRegistry registry, LogicEngine engine, IReadOnlyList<CardPackContent> packs)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(packs);

        foreach (var pack in packs)
        {
            foreach (var (cardId, prefabIds) in pack.Declarations)
            {
                registry.DeclarePrefab(cardId, prefabIds);
            }

            foreach (var snapshot in pack.InlinePrefabs)
            {
                engine.Prefabs.RegisterPrefab(snapshot);
            }

            if (pack.EffectsDirectory is { } directory)
            {
                var report = engine.Prefabs.LoadDirectory(directory);
                if (report.Failures.Count > 0)
                {
                    throw new InvalidDataException($"卡包 '{pack.Manifest.Id}' 效果库装载失败："
                        + string.Join("；", report.Failures));
                }
            }
        }
    }
}
