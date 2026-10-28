using System.Text.Json;
using System.Text.Json.Serialization;
using Orc.Core;

namespace Orc.Game.EffectParsing.Templates;

/// <summary>
/// 模板效果的 JSON 读写（转换段 S2）：明文 UTF-8、camelCase；
/// 反序列化 fail-fast（结构 + 槽位语义校验失败＝抛 <see cref="FormatException"/>）。
/// </summary>
public static class EffectTemplateJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions WriteIndentedOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>序列化模板效果。</summary>
    /// <exception cref="ArgumentNullException">template 为 null。</exception>
    public static string Serialize(EffectTemplate template, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(template);

        var dto = new TemplateDto
        {
            SchemaVersion = template.SchemaVersion,
            Id = template.Id,
            ActorFrom = template.ActorFrom == ActorFrom.EffectHost ? "effectHost" : "eventCard",
            Slots = template.Slots.Select(slot => new SlotDto { Name = slot.Name, Target = slot.Target }).ToList(),
            Root = ToDto(template.Root),
        };

        return JsonSerializer.Serialize(dto, indented ? WriteIndentedOptions : WriteOptions);
    }

    /// <summary>反序列化模板效果（fail-fast）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    /// <exception cref="FormatException">JSON 非法、结构缺项或槽位语义不合规。</exception>
    public static EffectTemplate Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        TemplateDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TemplateDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"模板效果 JSON 非法：{ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FormatException("模板效果为空。");
        }

        if (dto.Root is null)
        {
            throw new FormatException("模板效果缺少 root。");
        }

        if (dto.ActorFrom is not null and not ("eventCard" or "effectHost"))
        {
            throw new FormatException($"模板效果的 actorFrom 非法：'{dto.ActorFrom}'（应为 eventCard 或 effectHost）。");
        }

        EffectTemplate template;
        try
        {
            template = new EffectTemplate(
                dto.SchemaVersion,
                dto.Id ?? string.Empty,
                (dto.Slots ?? new List<SlotDto>())
                    .Select(slot => new EffectTemplateSlot(slot.Name ?? string.Empty, slot.Target ?? string.Empty))
                    .ToList(),
                FromDto(dto.Root),
                dto.ActorFrom == "effectHost" ? ActorFrom.EffectHost : ActorFrom.EventCard);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentNullException or ArgumentOutOfRangeException)
        {
            throw new FormatException($"模板效果结构非法：{ex.Message}", ex);
        }

        var errors = EffectTemplateSlots.Validate(template);
        if (errors.Count > 0)
        {
            throw new FormatException($"模板效果 '{template.Id}' 非法：{string.Join(" ", errors)}");
        }

        return template;
    }

    /// <summary>反序列化模板效果（结构化：失败不抛）。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    public static bool TryDeserialize(string json, out EffectTemplate? template, out string? error)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            template = Deserialize(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or ArgumentNullException or ArgumentOutOfRangeException)
        {
            template = null;
            error = ex.Message;
            return false;
        }
    }

    private static RootDto ToDto(EffectTemplateRoot root) => new()
    {
        MainTrigger = ToDto(root.MainTrigger),
        OtherTriggers = root.OtherTriggers.Select(ToDto).ToList(),
        Modings = root.Modings.Select(m => new ModingDto
        {
            Target = m.TargetEventId,
            Replacement = ToDto(m.Replacement),
        }).ToList(),
        Injects = root.Injects.Select(i => new InjectDto
        {
            Target = i.TargetTriggerName,
            Band = i.BandName,
            Event = i.EventId,
            Priority = i.Priority,
        }).ToList(),
    };

    private static TriggerDto ToDto(EffectTemplateTrigger trigger) => new()
    {
        Id = trigger.Id,
        StableKey = trigger.StableKey,
        Kind = trigger.IsPassive ? "passive" : "active",
        ViewType = trigger.ViewTypeName,
        Hooks = trigger.Hooks.ToList(),
        Events = trigger.Events.Select(ToDto).ToList(),
    };

    private static EventDto ToDto(EffectTemplateEvent ev) => new()
    {
        Id = ev.Id,
        Entry = ev.Entry,
        Csx = ev.Csx,
    };

    private static EffectTemplateRoot FromDto(RootDto dto)
    {
        if (dto.MainTrigger is null)
        {
            throw new FormatException("模板效果缺少 mainTrigger。");
        }

        var others = (dto.OtherTriggers ?? new List<TriggerDto>()).Select(FromDto).ToList();
        var modings = (dto.Modings ?? new List<ModingDto>())
            .Select(m => m.Replacement is null
                ? throw new FormatException("模板 moding 缺少 replacement。")
                : new EffectTemplateModing(m.Target ?? string.Empty, FromDto(m.Replacement)))
            .ToList();
        var injects = (dto.Injects ?? new List<InjectDto>())
            .Select(i => new EffectTemplateInject(i.Target ?? string.Empty, i.Band, i.Event ?? string.Empty, i.Priority))
            .ToList();

        return new EffectTemplateRoot(FromDto(dto.MainTrigger), others, modings, injects);
    }

    private static EffectTemplateTrigger FromDto(TriggerDto dto)
    {
        var isPassive = string.Equals(dto.Kind, "passive", StringComparison.OrdinalIgnoreCase);

        return new EffectTemplateTrigger(
            dto.Id ?? string.Empty,
            dto.StableKey ?? string.Empty,
            isPassive,
            dto.Hooks,
            (dto.Events ?? new List<EventDto>()).Select(FromDto),
            string.IsNullOrWhiteSpace(dto.ViewType) ? "Orc.Cards.CardEventView" : dto.ViewType!);
    }

    private static EffectTemplateEvent FromDto(EventDto dto) =>
        new(dto.Id ?? string.Empty, string.IsNullOrWhiteSpace(dto.Entry) ? "HandleAsync" : dto.Entry!, dto.Csx);

    private sealed class TemplateDto
    {
        public int SchemaVersion { get; set; }

        public string? Id { get; set; }

        /// <summary>施动卡来源（"eventCard"｜"effectHost"；缺省 eventCard）。</summary>
        public string? ActorFrom { get; set; }

        public List<SlotDto>? Slots { get; set; }

        public RootDto? Root { get; set; }
    }

    private sealed class SlotDto
    {
        public string? Name { get; set; }

        public string? Target { get; set; }
    }

    private sealed class RootDto
    {
        public TriggerDto? MainTrigger { get; set; }

        public List<TriggerDto>? OtherTriggers { get; set; }

        public List<ModingDto>? Modings { get; set; }

        public List<InjectDto>? Injects { get; set; }
    }

    private sealed class TriggerDto
    {
        public string? Id { get; set; }

        public string? StableKey { get; set; }

        public string? Kind { get; set; }

        public string? ViewType { get; set; }

        public List<string>? Hooks { get; set; }

        public List<EventDto>? Events { get; set; }
    }

    private sealed class EventDto
    {
        public string? Id { get; set; }

        public string? Entry { get; set; }

        public string? Csx { get; set; }
    }

    private sealed class ModingDto
    {
        public string? Target { get; set; }

        public EventDto? Replacement { get; set; }
    }

    private sealed class InjectDto
    {
        public string? Target { get; set; }

        public string? Band { get; set; }

        public string? Event { get; set; }

        public int Priority { get; set; }
    }
}
