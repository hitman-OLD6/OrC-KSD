using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Orc.Protocol;
using Orc.Server;
using Orc.Server.Transport;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// NDJSON 帧健壮性（F13）：增量限长（不先缓冲整行）、BOM/CRLF/多字节/空行语义不变、超限即断开。
/// </summary>
public class NdjsonFramingTests
{
    [Fact]
    public async Task Reader_ParsesBomCrLfMultibyteAndEmptyLines()
    {
        var payload = new List<byte>();
        payload.AddRange([0xEF, 0xBB, 0xBF]); // BOM
        payload.AddRange(Encoding.UTF8.GetBytes("""{"text":"中文内容"}""" + "\r\n"));
        payload.AddRange(Encoding.UTF8.GetBytes("\n"));            // 空行
        payload.AddRange(Encoding.UTF8.GetBytes("""{"n":1}"""));    // 末行无换行

        await using var reader = new NdjsonReader(new MemoryStream(payload.ToArray()));

        var first = await reader.ReadAsync();
        Assert.Equal("中文内容", first!["text"]!.GetValue<string>());

        var second = await reader.ReadAsync();
        Assert.Equal(1, second!["n"]!.GetValue<int>());

        Assert.Null(await reader.ReadAsync());
    }

    /// <summary>超限必须在读取过程中失败（不把整条 2 MiB 报文读进内存）。</summary>
    [Fact]
    public async Task Reader_ThrowsIncrementally_OnOversizedFrame()
    {
        var stream = new CountingStream(2 * 1024 * 1024);

        await using var reader = new NdjsonReader(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync());

        Assert.True(
            stream.BytesRead <= Ndjson.MaxLineLength + 64 * 1024,
            $"读取了 {stream.BytesRead} 字节才失败（应在上限附近即失败）。");
    }

    /// <summary>真实链路：发 2 MiB 无换行数据 → 服务端断开；服务端仍能接受新连接。</summary>
    [Fact]
    public async Task Server_DisconnectsOnOversizedFrame()
    {
        using var temp = new TestSupport.TempDirectory("orc-framing");
        await using var host = await BootstrapAsync(temp.Path);

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync("127.0.0.1", host.Port);
            var stream = raw.GetStream();

            var junk = new byte[2 * 1024 * 1024];
            Array.Fill(junk, (byte)'a');
            await stream.WriteAsync(junk);

            // 服务端读到上限即失败并断开：这里应看到 EOF（而不是无限等待）。
            var buffer = new byte[1024];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var closed = false;
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                    {
                        closed = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 未断开。
            }
            catch (IOException)
            {
                closed = true;
            }

            Assert.True(closed, "服务端未在超限帧上断开连接。");
        }

        // 进程没被打挂：新连接照常可用。
        await using var client = await TestSupport.ConnectAsync(host.Port);
        var info = await client.CallAsync("room.info");
        Assert.True(info.Ok, info.Message);
    }

    private static Task<ServerHost> BootstrapAsync(string sandboxRoot)
        => ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = sandboxRoot,
            Port = 0,
        });

    /// <summary>按需产出 <c>'a'</c> 的流；记录被读走的字节数（用于证明"增量失败"）。</summary>
    private sealed class CountingStream(long length) : Stream
    {
        private readonly byte[] _chunk = new byte[8192];

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - BytesRead;
            if (remaining <= 0)
            {
                return 0;
            }

            var take = (int)Math.Min(Math.Min(count, _chunk.Length), remaining);
            Array.Fill(buffer, (byte)'a', offset, take);
            BytesRead += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = length - BytesRead;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var take = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..take].Fill((byte)'a');
            BytesRead += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
