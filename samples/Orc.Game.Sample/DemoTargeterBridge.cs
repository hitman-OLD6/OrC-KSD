using Orc.Core;
using Orc.Game.Targeting;

namespace Orc.Game.Sample;

/// <summary>
/// 示例前端桥接（mock UI）：实现 <see cref="ITargeterBridge.BeginTargeting"/>——
/// 拿到会话后按队列逐个取选择器（<see cref="ITargeterSession.NextAsync"/>）、"运行视觉"、提交语义事件。
/// **自动模式**＝按策略应答；**交互模式**＝读控制台输入。
/// 用途＝示例宿主（**非生产实现**；生产侧由真实前端实现本接口）。
/// </summary>
internal sealed class DemoTargeterBridge : ITargeterBridge
{
    private readonly bool _interactive;

    /// <summary>创建示例桥接。</summary>
    /// <param name="interactive">true＝读控制台输入应答；false＝自动应答（无人值守演示）。</param>
    public DemoTargeterBridge(bool interactive) => _interactive = interactive;

    /// <summary>所服务的对局（示例保留字段）。</summary>
    public Match? Match { get; set; }

    /// <summary>自动模式下 mulligan 换牌张数（示例用；0＝不换）。</summary>
    public int AutoReplaceCount { get; set; }

    /// <inheritdoc />
    public void BeginTargeting(ITargeterSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _ = DriveAsync(session);
    }

    private async Task DriveAsync(ITargeterSession session)
    {
        try
        {
            while (true)
            {
                var selector = await session.NextAsync().ConfigureAwait(false);
                if (selector is null)
                {
                    break;
                }

                Present(selector);

                if (_interactive)
                {
                    InteractiveRespond(selector);
                }
                else
                {
                    AutoRespond(selector);
                }
            }

            if (session.Result is { } result)
            {
                Console.WriteLine($"  [终局] {result.Status}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [桥接异常] {ex.Message}");
        }
    }

    private static void Present(ISelectorInstance selector)
    {
        var p = selector.Presentation;
        var parameter = p.HasParameter ? $"、参数={Describe(p.Parameter)}" : string.Empty;
        Console.WriteLine($"  [交互] 选择器 '{p.SelectorName}'：模式={p.Mode}、数量={p.Min}..{p.Max}{parameter}");

        if (p.Identifiers is { Count: > 0 } identifiers)
        {
            foreach (var identifier in identifiers)
            {
                Console.WriteLine($"    选项：{identifier}");
            }
        }

        for (var i = 0; i < p.Candidates.Count; i++)
        {
            Console.WriteLine($"    [{i + 1}] {Describe(p.Candidates[i].Value)}");
        }
    }

    private void AutoRespond(ISelectorInstance selector)
    {
        var p = selector.Presentation;
        var isMulligan = selector.SelectorName == SelectorNames.Mulligan;
        var want = isMulligan ? AutoReplaceCount : Math.Max(p.Min, 1);

        bool accepted;
        if (p.Identifiers is { Count: > 0 } identifiers)
        {
            accepted = selector.Submit(new PickEvent(identifiers: identifiers.Take(Math.Min(want, identifiers.Count)).ToArray()));
        }
        else if (isMulligan && want == 0)
        {
            accepted = selector.Submit(new PickEvent());
        }
        else if (selector.Mode == SelectorInteractionMode.Drag)
        {
            var picks = p.Candidates.Take(Math.Min(want, p.Candidates.Count)).ToArray();
            accepted = picks.Length == 0
                ? selector.Submit(new CancelEvent())
                : selector.Submit(new PickEvent(references: picks));
        }
        else
        {
            var picks = p.Candidates.Take(Math.Min(want, p.Candidates.Count)).ToArray();
            accepted = picks.Length == 0
                ? selector.Submit(new CancelEvent())
                : selector.Submit(new PickEvent(references: picks));
        }

        Console.WriteLine($"    [自动应答] 选中 {(accepted ? "已提交" : "被拒")}（候选 {p.Candidates.Count} 个）");
    }

    private static void InteractiveRespond(ISelectorInstance selector)
    {
        var p = selector.Presentation;
        while (true)
        {
            Console.Write("    输入序号（逗号分隔可多选；直接回车＝空选；c＝取消）> ");
            var line = Console.ReadLine();
            if (line is null)
            {
                return; // 输入流结束：不构成终局
            }

            line = line.Trim();
            if (line.Equals("c", StringComparison.OrdinalIgnoreCase))
            {
                selector.Submit(new CancelEvent());
                return;
            }

            if (line.Length == 0)
            {
                selector.Submit(new PickEvent());
                return;
            }

            if (p.Identifiers is { Count: > 0 } identifiers)
            {
                if (!TryParseIndexes(line, identifiers.Count, out var indexes))
                {
                    Console.WriteLine("    输入非法（须为范围内的序号）。");
                    continue;
                }

                selector.Submit(new PickEvent(identifiers: indexes.Select(i => identifiers[i - 1]).ToArray()));
                return;
            }

            if (!TryParseIndexes(line, p.Candidates.Count, out var refIndexes))
            {
                Console.WriteLine("    输入非法（须为范围内的序号）。");
                continue;
            }

            selector.Submit(new PickEvent(references: refIndexes.Select(i => p.Candidates[i - 1]).ToArray()));
            return;
        }
    }

    private static bool TryParseIndexes(string line, int count, out IReadOnlyList<int> indexes)
    {
        var result = new List<int>();
        foreach (var part in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var number) || number < 1 || number > count)
            {
                indexes = Array.Empty<int>();
                return false;
            }

            result.Add(number);
        }

        indexes = result;
        return true;
    }

    private static string Describe(object? value) => value switch
    {
        null => "<空>",
        Ref<Entity> reference => Describe(reference.Value),
        Orc.Game.Players.Hq hq => Describe(hq.Owner),
        Orc.Game.Players.Player player => player.Index == 0 ? "玩家A" : "玩家B",
        Orc.Cards.Card card => card.Name,
        Orc.Game.Board.FormationGap gap => $"插入位置[{gap.Ordinal}]",
        _ => value.ToString() ?? value.GetType().Name,
    };
}
