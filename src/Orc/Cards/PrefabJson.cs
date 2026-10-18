using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orc.Core;

namespace Orc.Cards;

/// <summary>
/// 效果快照 JSON 序列化器（S-C5/S-C10）：把 <see cref="EffectSnapshot"/>（嵌套三层预制体）导出/读回。
/// 形态：{ schemaVersion, root: { id, version, mainTrigger, otherTriggers[], wrappers[] } }；
/// 触发器＝{ id, stableKey, kind("active"/"passive"), viewType, hooks[], events[] }；
/// 事件＝{ id, entry, csx?, assemblyKey?, downstream[], version }；wrapper＝{ target, replacement }。
/// 版本（S-C10）：读出时 <c>schemaVersion</c> 与本版不符＝结构化失败（不静默降级）。
/// 明文约定：输入输出均为 UTF-8 文本（社区明文储存）。
/// </summary>
public static class PrefabJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>导出效果快照为 JSON 字符串。</summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public static string Serialize(EffectSnapshot snapshot, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var dto = new SnapshotDto
        {
            SchemaVersion = snapshot.Version,
            Root = ToDto(snapshot.Root),
        };

        return JsonSerializer.Serialize(dto, indented ? IndentedOptions : WriteOptions);
    }

    /// <summary>
    /// 落盘文本生成（批 2 加性辅助——**落盘文本单一真源**；批 5 起可见性放宽〔internal→public，加性〕
    /// 供离线编译驱动复用——落盘出口 <c>PrefabWriter</c> 与 <c>Orc.Game.EffectParsing.EffectCompilationDriver</c>
    /// 共用，防「第二套文本标准」漂移；既有 <see cref="Serialize"/> 默认行为不变）：
    /// 文本契约与卡侧硬契约对齐（2 空格缩进多行、LF、尾随换行、不转义非 ASCII）；键序确定（DTO 声明序）、
    /// null 值字段忽略（沿用既有序列化选项）。UTF-8 无 BOM 由文件写入面保证。
    /// </summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public static string SerializeForDisk(EffectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var dto = new SnapshotDto
        {
            SchemaVersion = snapshot.Version,
            Root = ToDto(snapshot.Root),
        };

        // 文本契约：统一 LF（写出器平台换行〔Windows＝CRLF〕经规范化——跨平台输出一致）、尾随换行补足。
        // Replace 安全性：JSON 字符串内的 CR/LF 必为转义序列（控制字符始终转义）——原始 CR 仅出现在格式换行处。
        var json = JsonSerializer.Serialize(dto, DiskOptions);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>读回效果快照（失败＝抛，fail-fast）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    /// <exception cref="FormatException">JSON 非法、schema 版本不符或结构缺项。</exception>
    public static EffectSnapshot Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SnapshotDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SnapshotDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"效果快照 JSON 非法：{ex.Message}", ex);
        }

        if (dto?.Root is null)
        {
            throw new FormatException("效果快照缺少 root。");
        }

        // E-D1：接受 2（旧版：缺 disposingTrigger/oneShot/band）并按默认补齐；内存快照归一化为 3，写出恒为 3。
        if (dto.SchemaVersion is not (2 or EffectSnapshot.SchemaVersion))
        {
            throw new FormatException(
                $"效果快照 schema 版本不符：期望 2 或 {EffectSnapshot.SchemaVersion}，实际 {dto.SchemaVersion}（不静默降级）。");
        }

        return new EffectSnapshot(FromDto(dto.Root), EffectSnapshot.SchemaVersion);
    }

    /// <summary>读回效果快照（结构化：失败不抛）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static bool TryDeserialize(string json, out EffectSnapshot? snapshot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            snapshot = Deserialize(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or ArgumentNullException or ArgumentOutOfRangeException)
        {
            snapshot = null;
            error = ex.Message;
            return false;
        }
    }

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// 落盘文本选项（批 2 加性辅助）：缩进多行 ＋ 宽松编码器（不转义非 ASCII/HTML 敏感字符——
    /// 对齐既有样本实态与卡侧「不转义非 ASCII」口径；仅落盘出口使用，不影响既有 API 输出）。
    /// </summary>
    private static readonly JsonSerializerOptions DiskOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------- 领域 → DTO ----------

    private static EffectPrefabDto ToDto(EffectPrefab prefab) => new()
    {
        Id = prefab.Id,
        Version = prefab.Version,
        MainTrigger = ToDto(prefab.MainTrigger),
        OtherTriggers = prefab.OtherTriggers.Select(ToDto).ToList(),
        Wrappers = prefab.Wrappers.Select(w => new WrapperDto
        {
            Phase = w.Phase,
            Target = w.TargetEventId,
            Seam = w.SeamName,
            Handler = ToDto(w.Handler),
        }).ToList(),
        Injects = prefab.Injects.Select(i => new InjectPrefabDto
        {
            Target = i.TargetTriggerName,
            Band = i.BandName,
            Event = i.EventId,
            Priority = i.Priority,
        }).ToList(),
        DisposingTrigger = ToDto(prefab.DisposingTrigger),
        OneShot = prefab.OneShot,
    };

    private static TriggerPrefabDto ToDto(TriggerPrefab prefab) => new()
    {
        Id = prefab.Id,
        StableKey = prefab.StableKey,
        Kind = prefab.Kind == TriggerKind.Passive ? "passive" : "active",
        ViewType = prefab.ViewTypeName,
        Hooks = prefab.Hooks.ToList(),
        Events = prefab.Events.Select(ToDto).ToList(),
        Version = prefab.Version,
    };

    private static EventPrefabDto ToDto(EventPrefab prefab) => new()
    {
        Id = prefab.Id,
        Entry = prefab.EntryName,
        Csx = prefab.CsxSource,
        AssemblyKey = prefab.AssemblyKey,
        Downstream = prefab.Downstream.ToList(),
        Seams = prefab.Seams.ToList(),
        Version = prefab.Version,
        Band = prefab.Band,
    };

    // ---------- DTO → 领域 ----------

    private static EffectPrefab FromDto(EffectPrefabDto dto)
    {
        if (dto.MainTrigger is null)
        {
            throw new FormatException("效果预制体缺少 mainTrigger。");
        }

        var others = (dto.OtherTriggers ?? new List<TriggerPrefabDto>()).Select(FromDto).ToList();
        var wrappers = (dto.Wrappers ?? new List<WrapperDto>()).Select(w =>
        {
            if (w.Handler is null)
            {
                throw new FormatException("包装器缺少 handler。");
            }

            if (string.IsNullOrWhiteSpace(w.Phase))
            {
                throw new FormatException("包装器缺少 phase（before／after／replace）。");
            }

            return new WrapperPrefab(w.Phase!, w.Target ?? string.Empty, w.Seam, FromDto(w.Handler));
        }).ToList();

        List<InjectPrefab> injects;
        try
        {
            injects = (dto.Injects ?? new List<InjectPrefabDto>())
                .Select(i => new InjectPrefab(
                    i.Target ?? string.Empty, i.Band, i.Event ?? string.Empty, i.Priority))
                .ToList();
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"效果快照的 inject 声明非法：{ex.Message}", ex);
        }

        return new EffectPrefab(
            dto.Id ?? string.Empty,
            FromDto(dto.MainTrigger),
            others,
            wrappers,
            dto.Version <= 0 ? 1 : dto.Version,
            injects,
            dto.DisposingTrigger is null ? null : FromDto(dto.DisposingTrigger),
            dto.OneShot);
    }

    private static TriggerPrefab FromDto(TriggerPrefabDto dto)
    {
        if (dto.Events is null)
        {
            throw new FormatException($"触发器预制体 '{dto.Id}' 缺少 events。");
        }

        var kind = string.Equals(dto.Kind, "passive", StringComparison.OrdinalIgnoreCase)
            ? TriggerKind.Passive
            : TriggerKind.Active;

        return new TriggerPrefab(
            dto.Id ?? string.Empty,
            dto.StableKey ?? string.Empty,
            kind,
            dto.Hooks,
            dto.Events.Select(FromDto),
            dto.Version <= 0 ? 1 : dto.Version,
            string.IsNullOrWhiteSpace(dto.ViewType) ? "Orc.Cards.CardEventView" : dto.ViewType!);
    }

    private static EventPrefab FromDto(EventPrefabDto dto) => new(
        dto.Id ?? string.Empty,
        string.IsNullOrWhiteSpace(dto.Entry) ? "HandleAsync" : dto.Entry!,
        dto.Csx,
        dto.AssemblyKey,
        dto.Downstream,
        dto.Version <= 0 ? 1 : dto.Version,
        dto.Band,
        dto.Seams);

    // ---------- DTO 定义（可空字段容忍缺项，结构校验在领域构造期完成） ----------

    private sealed class SnapshotDto
    {
        public int SchemaVersion { get; set; }
        public EffectPrefabDto? Root { get; set; }
    }

    private sealed class EffectPrefabDto
    {
        public string? Id { get; set; }
        public int Version { get; set; }
        public TriggerPrefabDto? MainTrigger { get; set; }
        public List<TriggerPrefabDto>? OtherTriggers { get; set; }
        public List<WrapperDto>? Wrappers { get; set; }
        public List<InjectPrefabDto>? Injects { get; set; }
        public TriggerPrefabDto? DisposingTrigger { get; set; }
        public bool OneShot { get; set; }
    }

    private sealed class TriggerPrefabDto
    {
        public string? Id { get; set; }
        public string? StableKey { get; set; }
        public string? Kind { get; set; }
        public string? ViewType { get; set; }
        public List<string>? Hooks { get; set; }
        public List<EventPrefabDto>? Events { get; set; }
        public int Version { get; set; }
    }

    private sealed class EventPrefabDto
    {
        public string? Id { get; set; }
        public string? Entry { get; set; }
        public string? Csx { get; set; }
        public string? AssemblyKey { get; set; }
        public List<string>? Downstream { get; set; }
        public List<string>? Seams { get; set; }
        public int Version { get; set; }
        public string? Band { get; set; }
    }

    private sealed class WrapperDto
    {
        public string? Phase { get; set; }
        public string? Target { get; set; }
        public string? Seam { get; set; }
        public EventPrefabDto? Handler { get; set; }
    }

    private sealed class InjectPrefabDto
    {
        public string? Target { get; set; }
        public string? Band { get; set; }
        public string? Event { get; set; }
        public int Priority { get; set; }
    }
}
