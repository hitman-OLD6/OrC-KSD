using System.Text;
using System.Text.Json.Nodes;

namespace Orc.Protocol;

/// <summary>
/// 行分隔 JSON（NDJSON）帧编解码：一行一帧、UTF-8、LF 结尾。
/// <para>
/// BOM 由读取端剥离（与既有 stdio 适配层一致）；空行跳过。
/// 单行长度上限用于防止恶意/错误报文耗尽内存。
/// </para>
/// </summary>
public static class Ndjson
{
    /// <summary>单帧最大字符数（近似字节上限；1 MiB）。</summary>
    public const int MaxLineLength = 1 << 20;
}

/// <summary>NDJSON 读取器（一次一行；EOF 返回 null）。</summary>
public sealed class NdjsonReader : IAsyncDisposable
{
    private readonly StreamReader _reader;

    public NdjsonReader(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: leaveOpen);
    }

    /// <summary>
    /// 读取下一帧。流结束返回 <c>null</c>；空行跳过。
    /// </summary>
    /// <exception cref="InvalidDataException">单帧超过 <see cref="Ndjson.MaxLineLength"/>。</exception>
    public async Task<JsonNode?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length > Ndjson.MaxLineLength)
            {
                throw new InvalidDataException($"单帧长度 {line.Length} 超过上限 {Ndjson.MaxLineLength}。");
            }

            if (line.Length == 0)
            {
                continue;
            }

            return JsonNode.Parse(line);
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>NDJSON 写入器（串行化并发写，避免多推送交错）。</summary>
public sealed class NdjsonWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _leaveOpen;
    private bool _disposed;

    public NdjsonWriter(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>写出一个 JSON 对象（追加 LF 并刷新）。</summary>
    public async Task WriteAsync(JsonNode? node, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var json = node?.ToJsonString(ProtocolJson.Options) ?? "null";
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }
}
