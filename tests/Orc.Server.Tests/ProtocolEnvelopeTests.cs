using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;
using Orc.Protocol;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S2 协议信封：序列化形状、错误通道分层、NDJSON 帧、业务码与 McpCodes 的一致性。
/// </summary>
public class ProtocolEnvelopeTests
{
    [Fact]
    public void Request_Serializes_WithJsonRpcFieldName()
    {
        var request = new RpcRequest
        {
            Id = "1",
            Method = ProtocolMethods.ToolsCall,
            Params = new JsonObject { ["name"] = "room.info" },
        };

        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("1", root.GetProperty("id").GetString());
        Assert.Equal("tools/call", root.GetProperty("method").GetString());
        Assert.Equal("room.info", root.GetProperty("params").GetProperty("name").GetString());
    }

    [Fact]
    public void Request_RoundTrips()
    {
        var original = new RpcRequest
        {
            Id = "abc",
            Method = ProtocolMethods.Initialize,
            Params = new JsonObject { ["clientName"] = "godot" },
        };

        var json = JsonSerializer.Serialize(original, ProtocolJson.Options);
        var restored = JsonSerializer.Deserialize<RpcRequest>(json, ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored!.Id);
        Assert.Equal(original.Method, restored.Method);
        Assert.Equal("godot", restored.Params?["clientName"]?.GetValue<string>());
    }

    [Fact]
    public void Response_Success_OmitsErrorChannel()
    {
        var response = RpcResponse.Success("1", new JsonObject { ["ok"] = true });

        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);

        Assert.DoesNotContain("\"error\"", json);
        Assert.Contains("\"result\"", json);
    }

    [Fact]
    public void Response_Failure_UsesErrorChannel_WithCode()
    {
        var response = RpcResponse.Failure("2", RpcErrorCodes.MethodNotFound, "未知方法。");

        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);
        using var document = JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal(RpcErrorCodes.MethodNotFound, error.GetProperty("code").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public void ToolResult_BusinessFailure_StaysInResult()
    {
        var payload = ToolResultPayload.Failure(ResultCodes.DeckInvalid, "张数不足。", new JsonArray("c1"));

        var json = JsonSerializer.Serialize(payload, ProtocolJson.Options);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ResultCodes.DeckInvalid, document.RootElement.GetProperty("code").GetString());
        Assert.Equal("c1", document.RootElement.GetProperty("details")[0].GetString());
    }

    [Fact]
    public void Notification_HasNoId()
    {
        var notification = new RpcNotification
        {
            Method = ProtocolMethods.NotifyView,
            Params = new JsonObject { ["seq"] = 3 },
        };

        var json = JsonSerializer.Serialize(notification, ProtocolJson.Options);

        Assert.DoesNotContain("\"id\"", json);
        Assert.Contains(ProtocolMethods.NotifyView, json);
    }

    [Fact]
    public async Task Ndjson_RoundTrips_MultipleFrames()
    {
        var stream = new MemoryStream();

        await using (var writer = new NdjsonWriter(stream))
        {
            await writer.WriteAsync(new JsonObject { ["a"] = 1 });
            await writer.WriteAsync(new JsonObject { ["a"] = 2 });
        }

        stream.Position = 0;
        await using var reader = new NdjsonReader(stream);

        Assert.Equal(1, (await reader.ReadAsync())!["a"]!.GetValue<int>());
        Assert.Equal(2, (await reader.ReadAsync())!["a"]!.GetValue<int>());
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Ndjson_SkipsBlankLines_AndStripsBom()
    {
        var payload = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("\n{\"x\":1}\n\n{\"x\":2}\n"))
            .ToArray();

        await using var reader = new NdjsonReader(new MemoryStream(payload));

        Assert.Equal(1, (await reader.ReadAsync())!["x"]!.GetValue<int>());
        Assert.Equal(2, (await reader.ReadAsync())!["x"]!.GetValue<int>());
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Ndjson_Rejects_OverlongFrame()
    {
        var overlong = new string('x', Ndjson.MaxLineLength + 1) + "\n";
        await using var reader = new NdjsonReader(new MemoryStream(Encoding.UTF8.GetBytes(overlong)));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public void ResultCodes_MatchMcpCodes_ForSharedNames()
    {
        var mcp = typeof(McpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);

        var protocol = typeof(ResultCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);

        foreach (var (name, value) in mcp)
        {
            if (protocol.TryGetValue(name, out var protocolValue))
            {
                Assert.Equal(value, protocolValue);
            }
        }
    }
}
