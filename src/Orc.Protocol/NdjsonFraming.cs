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
    /// <summary>单帧最大字节数（1 MiB；**按字节**计，中文报文更早触发上限）。</summary>
    public const int MaxLineLength = 1 << 20;
}

/// <summary>
/// NDJSON 读取器（一次一行；EOF 返回 null）。
/// <para>
/// **增量读取**：逐块扫描换行、累计到当前行缓冲，一旦超过 <see cref="Ndjson.MaxLineLength"/>
/// 立即抛 <see cref="InvalidDataException"/>——绝不先缓冲整行（否则"2 MiB 无换行"的报文会先吃满内存）。
/// </para>
/// <para>
/// BOM 剥离、<c>\r\n</c> 容忍、空行跳过与 UTF-8 多字节（跨块由整行解码保证）都与旧实现一致。
/// </para>
/// </summary>
public sealed class NdjsonReader : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _chunk = new byte[8192];
    private readonly MemoryStream _line = new();

    private int _chunkStart;
    private int _chunkEnd;
    private bool _firstLine = true;
    private bool _eof;

    public NdjsonReader(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// 读取下一帧。流结束返回 <c>null</c>；空行跳过。
    /// </summary>
    /// <exception cref="InvalidDataException">单帧超过 <see cref="Ndjson.MaxLineLength"/> 字节。</exception>
    public async Task<JsonNode?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var newline = Array.IndexOf(_chunk, (byte)'\n', _chunkStart, _chunkEnd - _chunkStart);
            if (newline >= 0)
            {
                Append(_chunk, _chunkStart, newline - _chunkStart);
                _chunkStart = newline + 1;
                if (TryDecodeLine(out var framed))
                {
                    return framed;
                }

                continue; // 空行
            }

            if (_chunkEnd > _chunkStart)
            {
                Append(_chunk, _chunkStart, _chunkEnd - _chunkStart);
                _chunkStart = _chunkEnd;
            }

            if (_eof)
            {
                // 末行没有换行符：仍按一帧处理（与 StreamReader.ReadLine 的 EOF 语义一致）。
                return TryDecodeLine(out var tail) ? tail : null;
            }

            _chunkStart = _chunkEnd = 0;
            var read = await _stream.ReadAsync(_chunk.AsMemory(0, _chunk.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                _eof = true;
                continue;
            }

            _chunkEnd = read;
        }
    }

    /// <summary>追加到当前行缓冲；超过字节上限立刻失败（内存不随报文增长）。</summary>
    private void Append(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (_line.Length + count > Ndjson.MaxLineLength)
        {
            throw new InvalidDataException($"单帧长度超过上限 {Ndjson.MaxLineLength} 字节。");
        }

        _line.Write(buffer, offset, count);
    }

    /// <summary>解码当前行缓冲（是否产出帧）；随后清空缓冲。</summary>
    private bool TryDecodeLine(out JsonNode? node)
    {
        node = null;

        var length = (int)_line.Length;
        var buffer = _line.GetBuffer();

        var start = 0;
        if (_firstLine && length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            start = 3; // BOM 剥离
        }

        _firstLine = false;

        var end = length;
        while (end > start && buffer[end - 1] == (byte)'\r')
        {
            end--; // 容忍 CRLF
        }

        _line.SetLength(0);

        if (end == start)
        {
            return false; // 空行
        }

        node = JsonNode.Parse(Encoding.UTF8.GetString(buffer, start, end - start));
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _line.Dispose();
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
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
