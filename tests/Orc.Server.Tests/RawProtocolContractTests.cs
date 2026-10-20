using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 裸 socket 协议契约测试：**手写 JSON 报文，不经过 Orc.Protocol / Orc.Client 的任何 DTO**。
/// <para>
/// 存在的意义：客户端与服务端共用 DTO，若某字段名/类型系统性写错，双方会"一致地错"，
/// 基于 DTO 的 e2e 无法发现。这里只用手写字符串与 <see cref="JsonDocument"/>，
/// 验证线上真实形状（字段名、错误码数字、错误通道分层）。
/// </para>
/// </summary>
public class RawProtocolContractTests
{
    [Fact]
    public async Task RawInitialize_ReturnsPlainJsonRpcShape()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var connection = await RawConnection.ConnectAsync(host.Port);

        await connection.SendAsync(
            """{"jsonrpc":"2.0","id":"raw-1","method":"initialize","params":{"clientName":"raw","protocolVersion":"1.0"}}""");
        var line = await connection.ReadLineAsync();

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("raw-1", root.GetProperty("id").GetString());
        Assert.False(root.TryGetProperty("error", out _));

        var result = root.GetProperty("result");
        Assert.Equal("1.0", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("schemaVersion").GetInt32() > 0);
        Assert.Contains("room", result.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task RawUnknownMethod_ReturnsNumericMethodNotFound()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var connection = await RawConnection.ConnectAsync(host.Port);

        await connection.SendAsync("""{"jsonrpc":"2.0","id":"raw-2","method":"does/not/exist"}""");
        var line = await connection.ReadLineAsync();

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("raw-2", root.GetProperty("id").GetString());
        Assert.Equal(-32601, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(root.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task RawInvalidParams_ReturnsNumericInvalidParams()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var connection = await RawConnection.ConnectAsync(host.Port);

        await connection.SendAsync("""{"jsonrpc":"2.0","id":"raw-3","method":"tools/call","params":{}}""");
        var line = await connection.ReadLineAsync();

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal(-32602, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task RawBusinessFailure_StaysInResultChannel()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var connection = await RawConnection.ConnectAsync(host.Port);

        // room.claim 缺卡组 → 业务失败（不是协议错误）。
        await connection.SendAsync(
            """{"jsonrpc":"2.0","id":"raw-4","method":"tools/call","params":{"name":"room.claim","arguments":{"seat":0}}}""");
        var line = await connection.ReadLineAsync();

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("error", out _));

        var result = root.GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("deck-invalid", result.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RawToolsList_ReturnsArrayWithSchemaStrings()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var connection = await RawConnection.ConnectAsync(host.Port);

        await connection.SendAsync("""{"jsonrpc":"2.0","id":"raw-5","method":"tools/list"}""");
        var line = await connection.ReadLineAsync();

        using var document = JsonDocument.Parse(line);
        var tools = document.RootElement.GetProperty("result").EnumerateArray().ToArray();

        var roomInfo = tools.Single(tool => tool.GetProperty("name").GetString() == "room.info");
        Assert.Equal("room", roomInfo.GetProperty("group").GetString());
        Assert.False(string.IsNullOrWhiteSpace(roomInfo.GetProperty("inputSchema").GetString()));
    }

    [Fact]
    public async Task RawMalformedJson_ReturnsParseError()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        await using var connection = await RawConnection.ConnectAsync(host.Port);

        await connection.SendAsync("this is not json");
        var line = await connection.ReadLineAsync();

        using var document = JsonDocument.Parse(line);
        Assert.Equal(-32700, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>不依赖 Orc.Client 的原始连接（手写 JSON 行）。</summary>
    private sealed class RawConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;

        private RawConnection(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _reader = new StreamReader(stream, new UTF8Encoding(false));
        }

        public static async Task<RawConnection> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            return new RawConnection(client);
        }

        public Task SendAsync(string json) => _writer.WriteLineAsync(json);

        public async Task<string> ReadLineAsync()
        {
            var line = await _reader.ReadLineAsync();
            Assert.False(string.IsNullOrWhiteSpace(line), "服务端未返回任何帧。");
            return line!;
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            _reader.Dispose();
            _client.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc-raw-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }
}
