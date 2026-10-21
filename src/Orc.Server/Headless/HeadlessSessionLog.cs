using System.Text;
using System.Text.Json.Nodes;
using Orc.Output;

namespace Orc.Server.Headless;

/// <summary>
/// 无头会话流水（无头沙盒 S4）：全知事件流、可选按座位脱敏流、工具调用流水。
/// <para>
/// 三者都以"段"为单位顺序写；<see cref="StreamWriter.AutoFlush"/> 保证每写一行即落到 OS——
/// 强杀（<c>kill -9</c>）下最多丢"最后一段尚未 flush 的行"，断电/内核崩溃不承诺。
/// 终局额外 <c>Flush(true)</c>（fsync）。
/// </para>
/// </summary>
internal sealed class HeadlessSessionLog : IDisposable
{
    private readonly StreamWriter _events;
    private readonly StreamWriter _calls;
    private readonly StreamWriter? _seat0;
    private readonly StreamWriter? _seat1;

    public HeadlessSessionLog(string runDir, bool logSeatViews)
    {
        Directory.CreateDirectory(runDir);
        EventsPath = Path.Combine(runDir, "events.jsonl");
        CallsPath = Path.Combine(runDir, "calls.jsonl");

        _events = Open(EventsPath);
        _calls = Open(CallsPath);

        if (logSeatViews)
        {
            Seat0Path = Path.Combine(runDir, "seat0.jsonl");
            Seat1Path = Path.Combine(runDir, "seat1.jsonl");
            _seat0 = Open(Seat0Path);
            _seat1 = Open(Seat1Path);
        }
    }

    public string EventsPath { get; }

    public string CallsPath { get; }

    public string? Seat0Path { get; }

    public string? Seat1Path { get; }

    /// <summary>
    /// 落盘一批段（同一批只写一次全知流与两份脱敏流）。返回本批的段序号区间（无段＝<c>(null, null)</c>）。
    /// </summary>
    public (long? From, long? To) WriteSegments(IReadOnlyList<EventSegment> segments, Orc.Game.Match match)
    {
        if (segments.Count == 0)
        {
            return (null, null);
        }

        var visibility = new Orc.Game.Ui.MatchEventVisibility(match);

        foreach (var segment in segments)
        {
            // 段的分发（脱敏）与日志（全知）分开生成，不复用同一份 JSON 字符串。
            _events.WriteLine(EventSegmentJson.Serialize(segment, indented: false));

            _seat0?.WriteLine(EventSegmentJson.Serialize(segment, false, visibility, match.Players[0]));
            _seat1?.WriteLine(EventSegmentJson.Serialize(segment, false, visibility, match.Players[1]));
        }

        return (segments[0].Sequence, segments[^1].Sequence);
    }

    /// <summary>写一条工具调用记录（含门禁拒绝；字段固定，见实现文档 S4）。</summary>
    public void WriteCall(JsonObject record) => _calls.WriteLine(record.ToJsonString());

    /// <summary>把用户态缓冲刷到 OS（<paramref name="toDisk"/>＝同时 fsync）。</summary>
    public void Flush(bool toDisk)
    {
        foreach (var writer in Writers())
        {
            writer.Flush();
            if (toDisk)
            {
                if (writer.BaseStream is FileStream file) { file.Flush(flushToDisk: true); }
            }
        }
    }

    public void Dispose()
    {
        foreach (var writer in Writers())
        {
            try
            {
                writer.Dispose();
            }
            catch (IOException)
            {
                // 关闭路径忽略。
            }
        }
    }

    private IEnumerable<StreamWriter> Writers()
    {
        yield return _events;
        yield return _calls;

        if (_seat0 is not null)
        {
            yield return _seat0;
        }

        if (_seat1 is not null)
        {
            yield return _seat1;
        }
    }

    private static StreamWriter Open(string path)
        => new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
}
