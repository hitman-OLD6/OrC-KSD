using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S-C3 编排管理器验收（v4 形态）：节点＝带边的**成员**（不再登记触发器实例），
/// 实例＝该成员上的外来边，下游＝边声明的目标；反向依赖＝<c>AuditGraph.EdgesInto</c>。
/// </summary>
public class OrchestrationTests
{
    private static LogicEngine Subscribed(string signal, params string[] names)
    {
        var engine = new LogicEngine();
        foreach (var name in names)
        {
            SignalTestHelpers.Subscribe(engine, signal, name, new List<string>());
        }

        return engine;
    }

    [Fact]
    public void 订阅投影为成员节点()
    {
        var engine = Subscribed(Updates.CardPlaced, "订阅者");

        Assert.Equal(1, engine.Orchestration.Count);
        var node = engine.Orchestration.BuildAuditChain().Nodes.Single();
        Assert.Equal("engine#card.placed", node.DisplayName); // 展示名＝成员地址（不再有触发器显示名）
        Assert.Equal("passive", node.Kind);
        Assert.True(node.Instances.Single().Mounted);
        Assert.Equal("Explicit", node.Instances.Single().Origin);
        Assert.Equal("订阅者", node.Instances.Single().Host);
    }

    [Fact]
    public void 审查链_同一成员上的多条外来边聚合为单节点()
    {
        var engine = Subscribed(Updates.CardPlaced, "甲", "乙");

        var chain = engine.Orchestration.BuildAuditChain();

        var node = Assert.Single(chain.Nodes);
        Assert.Equal(2, node.Instances.Count);
        Assert.Equal(new[] { "甲", "乙" }, node.Instances.Select(i => i.Host));
        Assert.Equal(new[] { "甲", "乙" }, node.Events.Select(e => e.Name)); // 步骤＝边在成员内的执行序
        Assert.Equal(new[] { 1, 2 }, node.Events.Select(e => e.Seq));
    }

    [Fact]
    public void 反向依赖_按信号名查边()
    {
        var engine = new LogicEngine();
        SignalTestHelpers.Subscribe(engine, "shared.hook", "甲", new List<string>());
        SignalTestHelpers.Subscribe(engine, new[] { "shared.hook", "other.hook" }, "乙", new List<string>());
        SignalTestHelpers.Subscribe(engine, "other.hook", "丙", new List<string>());

        var subscribers = engine.BuildAuditGraph().EdgesInto("shared.hook");

        Assert.Equal(new[] { "甲", "乙" }, subscribers.Select(e => e.Member));
        Assert.Equal(2, engine.BuildAuditGraph().EdgesInto("other.hook").Count);
        Assert.Contains(engine.BuildAuditGraph().EdgesInto("other.hook"), e => e.Member == "丙");
    }

    [Fact]
    public void 审查链_可达链以成员为起点_未登记起点为空链()
    {
        var engine = Subscribed(Updates.CardPlaced, "订阅者");

        var reachable = engine.Orchestration.QueryChain(TriggerId.FromKey("engine#card.placed"));
        Assert.Equal(new[] { "engine#card.placed" }, reachable.Nodes.Select(n => n.StableKey));

        var unregistered = engine.Orchestration.QueryChain(TriggerId.FromKey("engine#unknown"));
        Assert.Empty(unregistered.Nodes);
    }

    [Fact]
    public void 审查链_携带schema版本与挂载边图()
    {
        var engine = Subscribed(Updates.CardPlaced, "订阅者");

        var chain = engine.Orchestration.BuildAuditChain();

        Assert.Equal(OrchestrationManager.SchemaVersion, chain.SchemaVersion);
        Assert.Equal(2, chain.SchemaVersion); // S10／A-4：四集合替代 hooks 段
        Assert.Contains(chain.Graph.EdgesInto(Updates.CardPlaced), e => e.Member == "订阅者");
    }

    [Fact]
    public void 无边的引擎不产生节点()
    {
        var engine = new LogicEngine();

        Assert.Equal(0, engine.Orchestration.Count);
        Assert.Empty(engine.Orchestration.BuildAuditChain().Nodes);
    }
}
