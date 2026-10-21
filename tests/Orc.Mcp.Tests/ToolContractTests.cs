using System.Text.Json;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 契约测试：统一返回外壳、结果码、工具目录与模式门控（R5/R0a/R2d）。
/// </summary>
public class ToolContractTests
{
    [Fact]
    public async Task ServiceInfo_ReportsModeVersionAndLimitations()
    {
        using var fixture = new SandboxFixture();

        var result = await fixture.InvokeAsync("service.info");

        Assert.True(result.Ok);
        Assert.Equal(McpCodes.Ok, result.Code);
        Assert.NotNull(result.Details);
        Assert.Equal("Authoring", result.Details!["mode"]!.GetValue<string>());
        Assert.Equal(fixture.Options.ContractSchemaVersion, result.Details!["contractSchemaVersion"]!.GetValue<int>());
        Assert.Equal(fixture.Service.Sandbox.Guard.Root.TrimEnd(Path.DirectorySeparatorChar),
            result.Details!["sandboxRoot"]!.GetValue<string>().TrimEnd(Path.DirectorySeparatorChar));

        var limitations = result.Details!["limitations"]!.AsArray();
        Assert.Contains(limitations, node => node!.GetValue<string>().Contains("插件不可热重启", StringComparison.Ordinal));
        Assert.Contains(limitations, node => node!.GetValue<string>().Contains("唯一活跃对局", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownTool_ReturnsControlledCode()
    {
        using var fixture = new SandboxFixture();

        var result = await fixture.InvokeAsync("nope.not-a-tool");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.UnknownTool, result.Code);
    }

    [Fact]
    public async Task InvalidArguments_ReturnControlledCode()
    {
        using var fixture = new SandboxFixture();

        var result = await fixture.InvokeAsync("match.concede", "{}");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.InvalidArguments, result.Code);
    }

    [Fact]
    public async Task CardLoad_WithoutMatch_ReturnsNotReady()
    {
        using var fixture = new SandboxFixture();

        var result = await fixture.InvokeAsync("card.load");

        Assert.False(result.Ok);
        Assert.Equal(McpCodes.NotReady, result.Code);
    }

    [Fact]
    public void AuthoringMode_ExposesSandboxTools()
    {
        using var fixture = new SandboxFixture();

        var names = fixture.Service.Tools.Select(tool => tool.Name).ToArray();

        Assert.Contains("sandbox.create", names);
        Assert.Contains("sandbox.emit", names);
        Assert.Contains("docs.list", names);
        Assert.Contains("diag.report", names);
        Assert.Contains("target.submit", names);
    }

    [Fact]
    public async Task PlayerMode_HidesSandboxTools()
    {
        using var fixture = new SandboxFixture(McpServiceMode.Player);

        var names = fixture.Service.Tools.Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("sandbox.create", names);
        Assert.DoesNotContain("sandbox.emit", names);
        Assert.DoesNotContain("diag.report", names);

        // 文档读取对两种模式都可见。
        Assert.Contains("docs.list", names);

        var result = await fixture.InvokeAsync("sandbox.create");
        Assert.False(result.Ok);
        Assert.Equal(McpCodes.UnknownTool, result.Code);
    }

    [Fact]
    public void AuthoringMode_ExposesSandboxPrimitiveTools()
    {
        using var fixture = new SandboxFixture();

        var names = fixture.Service.Tools.Select(tool => tool.Name).ToArray();

        // 六原语（R5）：每个原语一个工具，缺一即验收不成立。
        foreach (var primitive in new[]
                 {
                     "sandbox.instantiate", "sandbox.unitize", "sandbox.move",
                     "sandbox.remove", "sandbox.points", "sandbox.draw",
                 })
        {
            Assert.Contains(primitive, names);
        }

        // 旧五件套必须仍在（并存、不合并）。
        foreach (var legacy in new[]
                 {
                     "sandbox.spawn", "sandbox.kill", "sandbox.damage", "sandbox.set_points", "sandbox.emit",
                 })
        {
            Assert.Contains(legacy, names);
        }
    }

    [Fact]
    public void ToolDescriptors_HaveStableOrderAndSchemas()
    {
        using var first = new SandboxFixture();
        using var second = new SandboxFixture();

        var a = first.Service.Tools.Select(tool => tool.Name).ToArray();
        var b = second.Service.Tools.Select(tool => tool.Name).ToArray();

        Assert.Equal(a, b);
        Assert.All(first.Service.Tools, descriptor =>
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
            using var schema = JsonDocument.Parse(descriptor.InputSchema);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        });
    }

    [Fact]
    public async Task DocsListAndRead_AreUsable()
    {
        using var fixture = new SandboxFixture();

        var list = await fixture.InvokeAsync("docs.list");
        Assert.True(list.Ok);
        var documents = list.Details!["documents"]!.AsArray();
        Assert.NotEmpty(documents);

        var firstId = documents[0]!["id"]!.GetValue<string>();
        Assert.Contains(documents[0]!["sections"]!.AsArray(), node => node!["title"] is not null);

        var read = await fixture.InvokeAsync("docs.read", $"{{\"id\":\"{firstId}\",\"offset\":0,\"limit\":5}}");
        Assert.True(read.Ok);
        Assert.True(read.Details!["returnedLines"]!.GetValue<int>() > 0);
        Assert.False(string.IsNullOrWhiteSpace(read.Details!["content"]!.GetValue<string>()));

        var missing = await fixture.InvokeAsync("docs.read", "{\"id\":\"not-a-doc\"}");
        Assert.False(missing.Ok);
        Assert.Equal(McpCodes.InvalidArguments, missing.Code);
    }
}
