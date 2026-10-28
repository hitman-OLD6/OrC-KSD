namespace Orc.Game.Cards;

// ─────────────────────────────────────────────────────────────────────────────
// 卡牌效果声明面（原 `CardEffectLoader.cs` 的注册面；装载链已删除）：
//   `CardEffectRegistry`：卡 id → 效果预制体 id 清单（声明序）——**代码注册面的唯一声明入口**；
//   `CardEffectLoadContext`：卡加载时点读取"本卡声明了哪些效果 id"的上下文（由卡牌库注入）。
// 装配不在这里：声明的 id 由 `CardBase.LoadModelComponents` 直接进 v4 组件实体
// （目录直引，或旧快照经 `LegacySnapshotComponentBinder` 现转为 v4 组件）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 效果声明注册表（装配侧持有——注册源）：登记「卡 id → 效果预制体 id 清单」。
/// 注册即配置：卡 id 非法、清单含 null/空白或重复项、同一卡重复声明＝注册期明确拒绝（fail-fast、不吞）。
/// 标识是否可解析**不在声明期校验**（装载时按目录／快照查询）。
/// </summary>
public sealed class CardEffectRegistry
{
    private readonly Dictionary<string, IReadOnlyList<string>> _prefabDeclarations = new(StringComparer.Ordinal);

    /// <summary>
    /// 声明某卡（按卡库注册 id）的**效果预制体**清单（声明序＝装配顺序）。
    /// </summary>
    /// <exception cref="ArgumentException">cardId 为 null/空白；清单含 null/空白标识或重复项。</exception>
    /// <exception cref="ArgumentNullException">prefabIds 为 null。</exception>
    /// <exception cref="InvalidOperationException">同一卡重复声明（被拒绝——注册即配置）。</exception>
    public void DeclarePrefab(string cardId, IEnumerable<string> prefabIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentNullException.ThrowIfNull(prefabIds);

        var list = new List<string>();
        foreach (var prefabId in prefabIds)
        {
            if (string.IsNullOrWhiteSpace(prefabId))
            {
                throw new ArgumentException("效果预制体声明清单含 null/空白标识（配置错误在声明期被拒绝）。", nameof(prefabIds));
            }

            if (list.Contains(prefabId))
            {
                throw new ArgumentException(
                    $"效果预制体声明清单含重复项 '{prefabId}'（重复声明被拒绝——fail-fast）。", nameof(prefabIds));
            }

            list.Add(prefabId);
        }

        if (!_prefabDeclarations.TryAdd(cardId, list))
        {
            throw new InvalidOperationException($"卡牌 '{cardId}' 的效果预制体声明已存在（重复声明被拒绝——注册即配置）。");
        }
    }

    /// <summary>读取某卡的效果预制体声明（未声明＝空列表）。</summary>
    internal IReadOnlyList<string> GetPrefabDeclarations(string cardId)
        => _prefabDeclarations.TryGetValue(cardId, out var list) ? list : Array.Empty<string>();
}

/// <summary>
/// 效果装载上下文（卡加载时点的装配源面）：只提供「本卡声明了哪些效果 id」。
/// 由对局加载路径经卡牌库注入（延迟读取）；独立构造（无装配源）＝null＝跳过、不抛错、加载不失败。
/// </summary>
public sealed class CardEffectLoadContext
{
    private readonly CardEffectRegistry? _registry;
    private readonly string _cardId;

    internal CardEffectLoadContext(CardEffectRegistry? registry, string cardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        _registry = registry;
        _cardId = cardId;
    }

    /// <summary>本卡的效果预制体声明清单（声明序；无效果源或未声明＝空列表）。</summary>
    public IReadOnlyList<string> PrefabDeclarations
        => _registry?.GetPrefabDeclarations(_cardId) ?? Array.Empty<string>();
}
