using System.Text.Json.Nodes;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 诊断面（R1「可观测不可写」＋ R2 二选一取「列出并标注」）：
/// ①<code>diag.audit_chain</code> 顶层含 <c>reservedTriggers</c>（名／封闭态／授权／内置 handler 名）；
/// ②<code>diag.report.engine.triggers</code> 计数不含保留触发器（不与编排面混淆）。
/// 未授权标注在引擎级用例 <c>SandboxTriggerClosureTests.Export_MarksUnauthorized_AsUnavailable</c>（同一导出器）。
/// </summary>
public class DiagTests
{
    [Fact]
    public async Task AuditChain_ContainsReservedTriggers()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        var chain = await fixture.InvokeAsync("diag.audit_chain");
        Assert.True(chain.Ok, chain.Message);

        var reserved = chain.EngineResult!["reservedTriggers"]!.AsArray();
        Assert.Equal(6, reserved.Count);

        var names = reserved.Select(node => node!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("sandbox.instantiate", names);
        Assert.Contains("sandbox.points", names);

        foreach (var node in reserved)
        {
            Assert.True(node!["sealed"]!.GetValue<bool>(), "保留触发器应处于封闭态。");
            Assert.True(node["authorized"]!.GetValue<bool>(), "沙盒装配路径应为已授权。");
            var handlers = node["handlers"]!.AsArray();
            Assert.Single(handlers);
            Assert.False(string.IsNullOrWhiteSpace(handlers[0]!.GetValue<string>()));
        }

        // 既有审查链字段未被改写（增节是加性）。
        Assert.NotNull(chain.EngineResult!["nodes"]);
    }

    [Fact]
    public async Task Report_TriggerCountExcludesReserved()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        var report = await fixture.InvokeAsync("diag.report");
        Assert.True(report.Ok);

        var counted = report.Details!["engine"]!["triggers"]!.GetValue<int>();
        var orchestration = fixture.Service.Executor.Match!.Engine.Orchestration.Count;

        // 计数口径＝引擎编排触发器；保留触发器不进编排面（否则会多出 6）。
        Assert.Equal(orchestration, counted);
    }

    [Fact]
    public async Task AuditChain_ReservedTriggers_ReadOnly()
    {
        using var fixture = new SandboxFixture();
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        var before = fixture.Service.Executor.Match!.ExportReservedTriggersJson();
        var chain = await fixture.InvokeAsync("diag.audit_chain", """{"indented":true}""");
        var after = fixture.Service.Executor.Match!.ExportReservedTriggersJson();

        Assert.True(chain.Ok);
        Assert.Equal(before, after); // 导出是只读的，不改变任何装配事实

        var serialized = chain.EngineResult!.ToJsonString();
        Assert.Contains("reservedTriggers", serialized, StringComparison.Ordinal);
    }
}
