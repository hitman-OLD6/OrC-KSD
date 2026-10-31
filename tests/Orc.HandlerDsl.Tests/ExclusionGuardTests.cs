using System.Reflection;
using Orc.Game.Effects.HandlerDsl;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>
/// 排除清单与守护测试（S5／R27／D8／D10）：**一个测试、三态判定** ＋ **漏标断言** ＋ 文档／代码清单一致。
/// </summary>
public class ExclusionGuardTests
{
    private static readonly Assembly GameAssembly = typeof(Orc.Game.Effects.EffectRuntime).Assembly;

    [Fact]
    public void 三态判定_已登记门面的每个public方法都有归宿()
    {
        var result = HandlerDslCatalogScanner.Scan(GameAssembly);
        Assert.NotEmpty(result.Methods);

        var unresolved = result.Methods
            .Where(m => m.Outcome == HandlerDslMethodOutcome.Failed)
            .Select(m => $"{m.Face}.{m.Signature} → {m.Detail}")
            .ToArray();

        Assert.True(
            unresolved.Length == 0,
            "三态判定失败（①默认规则生成 ／ ②有标注 ／ ③在排除清单里，必居其一）：\n"
            + string.Join("\n", unresolved)
            + "\n建议：补 [HandlerDslNode]／[HandlerDslResult] 标注，或进 contracts/handler-dsl/v1/EXCLUSIONS.md。");
    }

    [Fact]
    public void 三态判定_每个方法都恰好落在三态之一()
    {
        var result = HandlerDslCatalogScanner.Scan(GameAssembly);
        var catalog = result.Catalog!;

        foreach (var method in result.Methods.Where(m => m.Outcome != HandlerDslMethodOutcome.Failed))
        {
            var inCatalog = method.NodeName is not null
                            && catalog.TryGetNode(method.NodeName, out var node)
                            && string.Equals(node.Method, method.Method, StringComparison.Ordinal)
                            && string.Equals(node.Face, method.Face.Split('.')[^1], StringComparison.Ordinal);

            var inExclusions = HandlerDslExclusions.TryFind(method.Signature, out _);

            Assert.True(
                inCatalog ^ inExclusions,
                $"{method.Face}.{method.Signature}："
                + $"在目录里={inCatalog}、在排除清单里={inExclusions}（必须恰居其一；Outcome={method.Outcome}）");
        }
    }

    [Fact]
    public void 漏标断言_名字匹配ActionFace的public类型必须登记()
    {
        var offenders = GameAssembly.GetTypes()
            .Where(static t => (t.IsPublic || t.IsNestedPublic) && t.Name.Contains("ActionFace", StringComparison.Ordinal))
            .Where(static t => t.GetCustomAttribute<HandlerDslActionFaceAttribute>(inherit: false) is null)
            .Select(static t => t.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "按约定新建门面却忘了登记（D10 漏标断言）：" + string.Join("、", offenders));
    }

    [Fact]
    public void 排除清单每条都有方法名_理由_替代路径()
    {
        Assert.NotEmpty(HandlerDslExclusions.All);

        foreach (var entry in HandlerDslExclusions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Signature));
            Assert.False(string.IsNullOrWhiteSpace(entry.Method));
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            Assert.False(string.IsNullOrWhiteSpace(entry.Alternative));
            Assert.True(entry.Reason.Length >= 12, $"'{entry.Method}' 的理由过短（不接受「暂不支持」一类空缺说法）：{entry.Reason}");

            // 替代路径必须可执行：节点组合（具体 op 序列）／表达式组合／补标注进目录。
            var actionable = entry.Alternative.Contains("节点", StringComparison.Ordinal)
                             || entry.Alternative.Contains("表达式", StringComparison.Ordinal)
                             || entry.Alternative.Contains("变体", StringComparison.Ordinal)
                             || (entry.Kind == HandlerDslExclusionKind.Deferred
                                 && entry.Alternative.Contains("进目录", StringComparison.Ordinal));
            Assert.True(actionable, $"'{entry.Method}' 的替代路径不可执行：{entry.Alternative}");
        }
    }

    [Fact]
    public void 代码侧清单与EXCLUSIONS点md同步_不许悄悄变长()
    {
        var markdown = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "EXCLUSIONS.md"));

        // 只取「A. 结构性排除」与「B. 延后」两张表的行：这两张才是三态判定用的清单
        // （「运行时能力现状」表里写的是能力现状，不是排除条目）。
        var start = markdown.IndexOf("## A.", StringComparison.Ordinal);
        var end = markdown.IndexOf("## 运行时能力现状", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "EXCLUSIONS.md 缺少 A/B 分区或运行时能力现状一节");

        var documented = markdown[start..end]
            .Split('\n')
            .Where(static line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(static line => line.Split('|')[1].Trim().Trim('`'))
            .Select(static cell => cell.Split('(')[0].Trim())
            .ToHashSet(StringComparer.Ordinal);

        var coded = HandlerDslExclusions.All.Select(static e => e.Method).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            coded.OrderBy(static n => n, StringComparer.Ordinal),
            documented.OrderBy(static n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void 延后条目必须写明可拆与补法_且核心动作优先补()
    {
        var deferred = HandlerDslExclusions.All
            .Where(e => e.Kind == HandlerDslExclusionKind.Deferred)
            .ToArray();

        Assert.NotEmpty(deferred);
        Assert.All(deferred, e =>
        {
            Assert.Contains("可以", e.Reason, StringComparison.Ordinal);
            Assert.Contains("本轮未拆", e.Reason, StringComparison.Ordinal);
            Assert.Contains("[HandlerDslResult]", e.Alternative, StringComparison.Ordinal);
        });

        // Join*Async 是把单位塞进战场的核心动作：延后条目里必须标优先补。
        Assert.All(
            deferred.Where(e => e.Method.StartsWith("Join", StringComparison.Ordinal)),
            e => Assert.True(e.Priority, $"'{e.Method}' 应标「优先补」"));

        // 结构性条目不得被误标成延后（理由写错会让后来者以为这条路走不通）。
        Assert.All(
            HandlerDslExclusions.All.Where(e => e.Kind == HandlerDslExclusionKind.Structural),
            e => Assert.DoesNotContain("本轮未拆", e.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void 排除清单不是缺口表_两者语义不同()
    {
        var markdown = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "EXCLUSIONS.md"));

        Assert.Contains("缺口表", markdown, StringComparison.Ordinal);
        Assert.Contains("不得合并", markdown, StringComparison.Ordinal);

        // 排除清单里不得出现「禁用态节点」的措辞——目录里要么可用、要么在清单里。
        Assert.DoesNotContain("disabled", markdown, StringComparison.OrdinalIgnoreCase);
    }
}
