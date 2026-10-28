using System.Text.Json;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S-C4 审查链导出验收（v4 形态：节点＝**成员**，实例＝该成员上的外来边）：
/// JSON 真源（结构／缺口／边四元组）与派生文本视图。
/// </summary>
public class AuditChainExportTests
{
    /// <summary>订阅一个**已声明**的内核信号（card.placed）：产生"端点实体 + engine#card.placed 边"。</summary>
    private static LogicEngine Subscribed(string name = "挂", string signal = Updates.CardPlaced)
    {
        var engine = new LogicEngine();
        SignalTestHelpers.Subscribe(engine, signal, name, new List<string>());
        return engine;
    }

    [Fact]
    public void JSON_空链_合法输出()
    {
        var engine = new LogicEngine();

        using var doc = JsonDocument.Parse(engine.ExportAuditChainJson());

        Assert.Equal(OrchestrationManager.SchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("edges").GetArrayLength());   // 无任何挂载边
        Assert.Equal(0, doc.RootElement.GetProperty("wraps").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("entities").GetArrayLength()); // 实体节点只有引擎（内核 4 信号已声明）
    }

    [Fact]
    public void JSON_节点以成员为粒度_实例与事件步骤完整表达()
    {
        using var doc = JsonDocument.Parse(Subscribed().ExportAuditChainJson());
        var nodes = doc.RootElement.GetProperty("nodes");

        var node = Assert.Single(nodes.EnumerateArray().ToArray());
        Assert.Equal("engine#card.placed", node.GetProperty("stableKey").GetString()); // 节点＝成员地址
        Assert.Equal("engine#card.placed", node.GetProperty("displayName").GetString());
        Assert.Equal("passive", node.GetProperty("kind").GetString());  // 信号成员＝等更新
        Assert.False(node.GetProperty("serializable").GetBoolean());
        Assert.True(node.GetProperty("declaredKey").GetBoolean());

        // 实例＝该成员上的一条外来边（来源实体／来源类别／装载态）。
        var instance = node.GetProperty("instances")[0];
        Assert.Equal("挂", instance.GetProperty("host").GetString());
        Assert.Equal("Explicit", instance.GetProperty("origin").GetString());
        Assert.True(instance.GetProperty("mounted").GetBoolean());

        // 事件步骤＝外来边在成员内的位次（1 起）与位次来源；旧 band 段号不再导出。
        var step = node.GetProperty("events")[0];
        Assert.Equal("挂", step.GetProperty("name").GetString());
        Assert.Equal(1, step.GetProperty("seq").GetInt32());
        Assert.Equal("appended", step.GetProperty("position").GetString());
        Assert.False(step.TryGetProperty("band", out _));

        // 下游＝边声明的目标地址（本成员自身；已解析）。
        var downstream = node.GetProperty("downstream")[0];
        Assert.Equal("engine#card.placed", downstream.GetProperty("key").GetString());
        Assert.NotEqual(JsonValueKind.Null, downstream.GetProperty("resolved").ValueKind);
    }

    [Fact]
    public void JSON_edges段_含目标来源与顺序来源()
    {
        // 未声明的信号名：边落在 hook#<名> 上、显式标为图缺口（"这个名字没人声明过"仍可读出）。
        var engine = new LogicEngine();
        SignalTestHelpers.Subscribe(engine, "h.json", "挂", new List<string>());

        using var doc = JsonDocument.Parse(engine.ExportAuditChainJson());
        var edge = doc.RootElement.GetProperty("edges")[0];

        // 目标、来源、位次来源、位次，四者一起才能推出执行序；来源是四类闭合枚举，不再有"无来源"。
        Assert.Equal("hook#h.json", edge.GetProperty("target").GetString());
        Assert.Equal("h.json", edge.GetProperty("targetMember").GetString());
        Assert.Equal("挂", edge.GetProperty("declaring").GetString());
        Assert.Equal("explicit", edge.GetProperty("source").GetString());
        Assert.Equal("appended", edge.GetProperty("orderSource").GetString());
        Assert.Equal(1, edge.GetProperty("position").GetInt32());
        Assert.True(edge.GetProperty("graphGap").GetBoolean()); // 未对应信号成员＝图缺口（显式呈现）

        // 同一条边挂在**已声明**信号上时：图缺口标记消失、目标为成员地址。
        using var declared = JsonDocument.Parse(Subscribed().ExportAuditChainJson());
        var signalEdge = declared.RootElement.GetProperty("edges")[0];
        Assert.Equal("engine#card.placed", signalEdge.GetProperty("target").GetString());
        Assert.False(signalEdge.GetProperty("graphGap").GetBoolean());
    }

    [Fact]
    public void JSON_含四集合与schema版本2()
    {
        using var doc = JsonDocument.Parse(Subscribed().ExportAuditChainJson());
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("entities").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("members").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("edges").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("wraps").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("nodes").ValueKind); // 能力不退化：成员图保留
        Assert.False(root.TryGetProperty("hooks", out _)); // 旧订阅读面不再导出（不留两套读面）
    }

    [Fact]
    public void 文本视图_含关键行()
    {
        var text = Subscribed().ExportAuditChainText();

        Assert.Contains("审查链 schemaVersion=2", text);
        Assert.Contains("[被动] engine#card.placed key=engine#card.placed", text);
        Assert.Contains("→ engine#card.placed (resolved ", text);
        Assert.Contains("serializable=false", text);
        Assert.Contains("origin=Explicit mounted=true", text);
        Assert.Contains("#1 挂 pos=appended", text);
    }

    [Fact]
    public void 可达链导出_以成员节点为起点且下游解析为自身()
    {
        var engine = Subscribed();

        using var doc = JsonDocument.Parse(engine.ExportChainJson(TriggerId.FromKey("engine#card.placed")));
        var keys = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("stableKey").GetString()).ToList();

        Assert.Equal(new[] { "engine#card.placed" }, keys);
    }

    [Fact]
    public void 可达链导出_起点未登记时为空链()
    {
        var engine = Subscribed();

        using var doc = JsonDocument.Parse(engine.ExportChainJson(TriggerId.FromKey("engine#not-a-member")));

        Assert.Equal(0, doc.RootElement.GetProperty("nodes").GetArrayLength());
    }
}
