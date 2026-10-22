using System.Text;
using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S2 父子私有通道：有 <c>id</c> ＝应答、无 <c>id</c> ＝事件；未知 op 有可读错误；stdout 不混日志。
/// </summary>
public class AdminChannelTests
{
    [Fact]
    public async Task Channel_AnswersWithId_AndUnknownOpIsRejectedReadably()
    {
        await using var fixture = await HostFixture.StartAsync();

        using var input = new MemoryStream();
        using var output = new MemoryStream();

        var channel = AdminChannel.Attach(input, output, fixture.Host);
        await using (channel)
        {
            channel.Start();
            Write(input, """{"id":1,"op":"instance.info"}""");
            Write(input, """{"id":2,"op":"no.such.op"}""");
            input.Position = 0;

            await channel.RunAsync();

            var lines = ReadLines(output);
            Assert.Equal(2, lines.Count);

            var first = lines[0];
            Assert.Equal(1, first["id"]!.GetValue<int>());
            Assert.True(first["ok"]!.GetValue<bool>());
            Assert.Equal(fixture.Host.Room.RoomId, first["payload"]!["roomId"]!.GetValue<string>());
            Assert.True(first["payload"]!["port"]!.GetValue<int>() > 0);

            var second = lines[1];
            Assert.Equal(2, second["id"]!.GetValue<int>());
            Assert.False(second["ok"]!.GetValue<bool>());
            Assert.Equal(ResultCodes.InvalidArguments, second["code"]!.GetValue<string>());
            Assert.Contains("no.such.op", second["message"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Channel_EmitsEventsWithoutId_AndKeepsStdoutPure()
    {
        await using var fixture = await HostFixture.StartAsync();

        using var input = new MemoryStream();
        using var output = new MemoryStream();

        var channel = AdminChannel.Attach(input, output, fixture.Host);
        await using (channel)
        {
            channel.Start();

            // 座位申请会触发 room.seats / seat.changed / conn.pending 三类事件（无 id）。
            await using var client = await fixture.ConnectAsync();
            await HostFixture.AttachControllerAsync(client, seat: 0);

            Write(input, """{"id":"a","op":"connections.list"}""");
            input.Position = 0;
            await channel.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var lines = ReadLines(output);
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            var hasId = line.ContainsKey("id") && line["id"] is not null;
            var hasEvent = line.ContainsKey("event");
            Assert.True(hasId ^ hasEvent, $"报文既非应答也非事件：{line.ToJsonString()}");
        }

        Assert.True(lines.Any(line => line["event"]?.GetValue<string>() == "conn.pending"), "未收到 conn.pending 事件");
        Assert.True(lines.Any(line => line["event"]?.GetValue<string>() == "room.seats"), "未收到 room.seats 事件");
    }

    private static void Write(Stream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static IReadOnlyList<JsonObject> ReadLines(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var text = reader.ReadToEnd();
        var lines = new List<JsonObject>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            lines.Add((JsonObject)JsonNode.Parse(line)!);
        }

        return lines;
    }
}
