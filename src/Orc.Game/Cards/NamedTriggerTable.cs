using System.Diagnostics.CodeAnalysis;

namespace Orc.Game.Cards;

/// <summary>
/// 具名触发器表（S5 具名触发面／指向承载的共享实现）：把触发器按名登记，供效果预制体 <c>injects</c>
/// 按名解析注入目标；<see cref="CardBase"/> 与 <see cref="Hq"/> 各持一份（HQ 不继承 CardBase，但需同一接入面）。
/// 重复登记＝后者覆盖（与既有 CardBase 行为一致）。
/// </summary>
public sealed class NamedTriggerTable
{
    private readonly Dictionary<string, (object Trigger, Type ViewType)> _entries = new(StringComparer.Ordinal);

    /// <summary>登记具名触发器。</summary>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="ArgumentNullException">trigger 或 viewType 为 null。</exception>
    public void Register(string name, object trigger, Type viewType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(viewType);

        _entries[name] = (trigger, viewType);
    }

    /// <summary>按名查找具名触发器（未登记＝false）。</summary>
    public bool TryFind(
        string name,
        [NotNullWhen(true)] out object? trigger,
        [NotNullWhen(true)] out Type? viewType)
    {
        trigger = null;
        viewType = null;

        if (string.IsNullOrWhiteSpace(name) || !_entries.TryGetValue(name, out var entry))
        {
            return false;
        }

        trigger = entry.Trigger;
        viewType = entry.ViewType;
        return true;
    }
}
