using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// R9 验收（归一化唯一入口）：<see cref="SourceNormalizer.ToCard"/> 的五类输入——
/// Card／存活 Ref／失效 Ref／其它对象／null；失效与错型降级 null 且留痕，null 为合法空来源。
/// </summary>
public class SourceNormalizerTests
{
    [Fact]
    public async Task Card_Input_Passes_Through()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "卡");
        Assert.Same(card, SourceNormalizer.ToCard(card, engine, "test"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Alive_Ref_Input_Resolves_To_Target_Card()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "卡");
        Assert.Same(card, SourceNormalizer.ToCard(card.Ref, engine, "test"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Stale_Ref_Input_Degrades_To_Null_With_Trace()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "将销毁");
        var reference = card.Ref;
        await engine.DestroyCard(card);

        Assert.Null(SourceNormalizer.ToCard(reference, engine, "test.stale"));

        var entry = Assert.Single(
            engine.RootStream.Entries, item => item.Keywords.Contains("source"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("test.stale", entry.Keywords);
        Assert.Contains("已失效", entry.Message);
    }

    [Fact]
    public async Task Other_Object_Input_Degrades_To_Null_With_Trace()
    {
        var engine = new LogicEngine();

        Assert.Null(SourceNormalizer.ToCard("不是卡", engine, "test.wrong"));

        var entry = Assert.Single(
            engine.RootStream.Entries, item => item.Keywords.Contains("source"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Null_Input_Is_Legal_Empty_Source_Without_Trace()
    {
        var engine = new LogicEngine();

        Assert.Null(SourceNormalizer.ToCard(null, engine, "test.null"));
        Assert.Empty(engine.RootStream.Entries); // 无来源＝合法空值（不产生告警噪音）
        await Task.CompletedTask;
    }
}
