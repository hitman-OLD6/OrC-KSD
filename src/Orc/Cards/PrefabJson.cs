using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orc.Core;

namespace Orc.Cards;

/// <summary>
/// 效果快照落盘序列化器（S-C5/S-C10；v4-only 后只保留**写出**一半）。
/// <para>
/// 形态：{ schemaVersion, root: { id, version, mainTrigger, otherTriggers[], wrappers[] } }；
/// 触发器＝{ id, stableKey, kind("active"/"passive"), viewType, hooks[], events[] }；
/// 事件＝{ id, entry, csx?, assemblyKey?, downstream[], version }；wrapper＝{ target, replacement }。
/// </para>
/// <para>
/// 唯一用途＝<c>LegacySnapshotComponentBinder</c> 的迁移输入（旧快照 → 旧格式 JSON → v4 组件）。
/// 读入（<c>Deserialize</c>／<c>DeserializeFile</c>）随旧装载链删除——资产面已切 v4，不再读旧格式。
/// </para>
/// </summary>
public static class PrefabJson
{
    /// <summary>
    /// 落盘文本生成（**落盘文本单一真源**；离线编译驱动与迁移桥共用，防「第二套文本标准」漂移）：
    /// 文本契约与卡侧硬契约对齐（2 空格缩进多行、LF、尾随换行、不转义非 ASCII）；键序确定（DTO 声明序）、
    /// null 值字段忽略。UTF-8 无 BOM 由文件写入面保证。
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

    /// <summary>
    /// **豁免保留**（本轮明确记账，见 PROGRESS §26）：把快照写成 JSON 文本（可缩进）。
    /// 消费者＝①卡数据 <c>components.effects.inline</c> 的写方向；②<see cref="EffectCompiler.CompileToJson"/>。
    /// 两者产出的都是**旧快照文本**，下一步仍由 <c>LegacySnapshotComponentBinder</c> 转 v4。
    /// </summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public static string Serialize(EffectSnapshot snapshot, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var dto = new SnapshotDto
        {
            SchemaVersion = snapshot.Version,
            Root = ToDto(snapshot.Root),
        };

        return JsonSerializer.Serialize(dto, indented ? IndentedOptions : DiskOptions);
    }

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// **豁免保留**（本轮明确记账，见 PROGRESS §26）：读回效果快照文本。
    /// 唯一消费者＝卡数据 <c>components.effects.inline</c> 的读方向（卡包内联效果仍以旧快照形态随卡分发）。
    /// 目录级旧资产装载已删除——不得再用它扫描 <c>*.prefab.json</c> 目录。
    /// </summary>
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

        if (dto.SchemaVersion is not (2 or EffectSnapshot.SchemaVersion))
        {
            throw new FormatException(
                $"效果快照 schema 版本不符：期望 2 或 {EffectSnapshot.SchemaVersion}，实际 {dto.SchemaVersion}（不静默降级）。");
        }

        return new EffectSnapshot(FromDto(dto.Root), EffectSnapshot.SchemaVersion);
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 落盘文本选项：缩进多行 ＋ 宽松编码器（不转义非 ASCII/HTML 敏感字符——
    /// 对齐既有样本实态与卡侧「不转义非 ASCII」口径）。
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
        JudicatorWrappers = prefab.JudicatorWrappers.Select(j => new JudicatorWrapperDto
        {
            Judicator = j.JudicatorName,
            Phase = j.Phase,
            Handler = ToDto(j.Handler),
        }).ToList(),
    };

    private static TriggerPrefabDto ToDto(TriggerPrefab prefab) => new()
    {
        Id = prefab.Id,
        StableKey = prefab.StableKey,
        Kind = prefab.IsPassive ? "passive" : "active",
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

    // ---------- DTO → 领域（**豁免读面**：仅卡体内联效果） ----------

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

        var judicatorWrappers = (dto.JudicatorWrappers ?? new List<JudicatorWrapperDto>())
            .Select(j =>
            {
                if (j.Handler is null)
                {
                    throw new FormatException("judicatorWrapper 缺少 handler。");
                }

                if (string.IsNullOrWhiteSpace(j.Judicator))
                {
                    throw new FormatException("judicatorWrapper 缺少 judicator。");
                }

                return new JudicatorWrapperPrefab(
                    j.Judicator!, j.Phase ?? "after", FromDto(j.Handler));
            })
            .ToList();

        return new EffectPrefab(
            dto.Id ?? string.Empty,
            FromDto(dto.MainTrigger),
            others,
            wrappers,
            dto.Version <= 0 ? 1 : dto.Version,
            injects,
            dto.DisposingTrigger is null ? null : FromDto(dto.DisposingTrigger),
            dto.OneShot,
            judicatorWrappers);
    }

    private static TriggerPrefab FromDto(TriggerPrefabDto dto)
    {
        if (dto.Events is null)
        {
            throw new FormatException($"触发器预制体 '{dto.Id}' 缺少 events。");
        }

        var isPassive = string.Equals(dto.Kind, "passive", StringComparison.OrdinalIgnoreCase);

        return new TriggerPrefab(
            dto.Id ?? string.Empty,
            dto.StableKey ?? string.Empty,
            isPassive,
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

    // ---------- DTO 定义（只写出，不需要读入容错） ----------

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
        public List<JudicatorWrapperDto>? JudicatorWrappers { get; set; }
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

    private sealed class JudicatorWrapperDto
    {
        public string? Judicator { get; set; }
        public string? Phase { get; set; }
        public EventPrefabDto? Handler { get; set; }
    }
}
