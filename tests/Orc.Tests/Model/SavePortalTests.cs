using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S1／S2 验收（纯内核路径）：
/// <list type="bullet">
/// <item>S1（D50／D52）：<c>save</c> 门户沿链继承与覆盖、装载期只校验形态；<see cref="PrefabType.Origin"/> 三态
/// （null＝手写／内建；inline／named 产物取源模板 id）；溯源公式 <c>Origin?.TemplateId ?? Type.Id</c>
/// 且 <c>inline/</c> 缓存键与产物资产 id **绝不**进公式结果。</item>
/// <item>S2（D55）：<c>slots</c> 与 <c>privateFields</c> 两张独立表（同名并存、互不覆盖）；
/// 存档读 <c>_state</c> 不读声明初值；实体引用转名、非实体明确失败；JSON 序列化确定性。</item>
/// </list>
/// </summary>
public sealed class SavePortalTests
{
    // ==================== S1：save 门户 ====================

    /// <summary>save 沿 Extends 链继承（父声明、子拥有）；子可覆盖为自己的服务。</summary>
    [Fact]
    public void Save_沿链继承_子可覆盖()
    {
        var catalog = Catalog();

        Assert.Equal("test.full", catalog.Resolve("unit.fire").Save);
        Assert.Equal("test.full", catalog.Resolve("unit.fire.child").Save); // 继承
        Assert.Equal("test.other", catalog.Resolve("unit.fire.override").Save); // 覆盖
        Assert.Null(catalog.Resolve("unit.nosave").Save); // 未声明＝不可存档
    }

    /// <summary>装载期只校验形态（D50）：含空白／分隔符＝拒绝；纯空白＝未声明（null）；**不**校验服务是否存在。</summary>
    [Fact]
    public void Save_装载期只校验形态_不校验服务存在性()
    {
        Assert.Throws<ArgumentException>(() => Catalog(save: "has space"));
        Assert.Throws<ArgumentException>(() => Catalog(save: "a:b/c"));

        // 纯空白＝未声明（Normalize 归 null），不是形态非法。
        Assert.Null(Catalog(save: "  ").Resolve("unit.fire").Save);

        // 未注册的服务 id：装载期合法（存在性在存档时刻解析）。
        Assert.Equal("no.such.service", Catalog(save: "no.such.service").Resolve("unit.fire").Save);
    }

    /// <summary>Origin 三态（D52）：手写资产＝null；inline／named 产物＝源模板 id（不是缓存键／产物 id）。</summary>
    [Fact]
    public void Origin_三态_手写为null_产物取源模板id()
    {
        Assert.Null(Catalog().Resolve("unit.fire").Origin); // 手写资产：null＝非实例化

        var template = ComponentTemplate(save: "test.full");
        var declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template };

        var inline = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = Args(),
            Mode = ExpansionMode.Inline,
            Declarations = declarations,
        });
        Assert.StartsWith("inline/", inline.CacheKey!, StringComparison.Ordinal);
        Assert.Equal(template.Id, inline.Expanded.Asset.Origin!.TemplateId); // 产物资产层已带 origin

        var named = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = Args(),
            Mode = ExpansionMode.Named,
            Id = "saved.echo.1",
            Declarations = declarations,
        });
        Assert.Equal(template.Id, named.Expanded.Asset.Origin!.TemplateId); // named 产物同样回溯源模板
        Assert.NotEqual("saved.echo.1", named.Expanded.Asset.Origin!.TemplateId); // 产物 id 不当锚点
    }

    /// <summary>
    /// 溯源唯一入口（D51／D52）：手写实体取 Type.Id；inline 产物实体取 Origin.TemplateId——
    /// 守护断言：<c>inline/&lt;id&gt;/&lt;hash&gt;</c> 缓存键**绝不**出现在公式结果里。
    /// </summary>
    [Fact]
    public void Extends公式_手写取TypeId_inline取源模板id_缓存键绝不进入()
    {
        var template = ComponentTemplate(save: "test.full");
        var catalog = Catalog().Add(template.ToPrefabDefinition(_ => throw new KeyNotFoundException("无 handler")));
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());

        // ① 手写资产实体：extends ＝ Type.Id。
        var handWritten = runtime.CreateEntity("unit.fire", "fire1");
        Assert.Equal("unit.fire", SaveContext.ExtendsOf(handWritten));

        // ② inline 产物实体：extends ＝ 源模板 id（产物以缓存键注册在对局级表）。
        var instance = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = Args(),
            Mode = ExpansionMode.Inline,
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
            InlineTable = runtime.InlinePrefabs,
        });
        var restored = runtime.CreateEntity(instance.CacheKey!, "echo1", handWritten);

        Assert.Equal(template.Id, SaveContext.ExtendsOf(restored));
        Assert.DoesNotContain("inline/", SaveContext.ExtendsOf(restored), StringComparison.Ordinal);
        Assert.DoesNotContain("saved.", SaveContext.ExtendsOf(restored), StringComparison.Ordinal);
    }

    // ==================== S2：两张独立的表 ====================

    /// <summary>
    /// D55 判据：存档读 <c>_state</c>、不读 <c>Type.Slots</c>——
    /// 同一 <c>hp</c> 名下，slots 是配置值（3）、privateFields 是运行时状态（1），两者并存、互不覆盖。
    /// </summary>
    [Fact]
    public void ReadState_读_state不读声明初值_两张表同名并存()
    {
        var catalog = Catalog(sameName: true);
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var entity = runtime.CreateEntity("unit.fire", "fire1");
        entity.SetPrivateField("hp", 1m);

        var context = NewContext(runtime, judicators: null);
        var slots = context.ReadSlots(entity.Type);
        var fields = context.ReadPrivateFields(entity);

        Assert.Equal(3m, slots["hp"]); // 配置值：资产级空位的绑定值
        Assert.Equal(1m, fields["hp"]); // 运行时状态：_state 当前值（不是声明初值 0）

        // 两张表互不兜底：各自保留（同名键在两侧各自出现）。
        Assert.True(slots.ContainsKey("hp"));
        Assert.True(fields.ContainsKey("hp"));
    }

    /// <summary>实体引用（S2）：entity 型值是 OrchestrationEntity ⇒ 存名字；非实体对象 ⇒ 明确失败（报字段名与类型名）。</summary>
    [Fact]
    public void ReadPrivateFields_实体引用转名_非实体对象失败()
    {
        var catalog = Catalog();
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var entity = runtime.CreateEntity("unit.fire", "fire1");
        var buddy = runtime.CreateEntity("unit.nosave", "buddy1");
        entity.SetPrivateField("buddy", buddy);

        var context = NewContext(runtime, judicators: null);
        Assert.Equal("buddy1", context.ReadPrivateFields(entity)["buddy"]);

        entity.SetPrivateField("buddy", new object()); // entity 字段接受任何非 null 对象（ViewSpec 语义）
        var error = Assert.Throws<InvalidOperationException>(() => context.ReadPrivateFields(entity));
        Assert.Contains("buddy", error.Message, StringComparison.Ordinal);
        Assert.Contains("Object", error.Message, StringComparison.Ordinal); // 报实际类型名
    }

    /// <summary>S2 序列化：同名键在两张表里各自保留；两次写出逐字节相同（键序确定、无浮点非确定性）。</summary>
    [Fact]
    public void SaveModingJson_两张表同名并存_写出确定()
    {
        var catalog = Catalog(sameName: true);
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var entity = runtime.CreateEntity("unit.fire", "fire1");
        entity.SetPrivateField("hp", 1m);

        var context = NewContext(runtime, judicators: null);
        var moding = new SaveModing(
            SaveContext.ExtendsOf(entity),
            context.ReadSlots(entity.Type),
            context.ReadPrivateFields(entity),
            Array.Empty<SaveAttachment>());

        var json = Write(moding);
        var repeated = Write(moding);
        Assert.Equal(json, repeated); // 确定性

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("unit.fire", doc.RootElement.GetProperty("extends").GetString());
        Assert.Equal(3m, doc.RootElement.GetProperty("slots").GetProperty("hp").GetDecimal()); // 配置值
        Assert.Equal(1m, doc.RootElement.GetProperty("privateFields").GetProperty("hp").GetDecimal()); // 运行时状态
    }

    /// <summary>值表面封闭（S2）：实体引用之外的非实体对象在序列化侧同样 fail-fast（双保险）。</summary>
    [Fact]
    public void SaveModingJson_越界值_failFast()
    {
        var moding = new SaveModing(
            "unit.fire",
            new Dictionary<string, object?> { ["bad"] = new object() },
            new Dictionary<string, object?>(),
            Array.Empty<SaveAttachment>());

        Assert.Throws<JsonException>(() => Write(moding));
    }

    // ==================== 场景 ====================

    /// <summary>
    /// 存档测试目录（内核目录＋unit 系预制体＋fx 附件）：内核引擎（4 信号）保证组件装配的
    /// 兜底信号成员在场；<c>fx.echo</c>（声明 save）与 <c>fx.nosave</c>（未声明）供附件层用例。
    /// </summary>
    internal static PrefabCatalog Catalog(string? save = null, bool sameName = false)
        => KernelSignals.CreateCatalog()
            .Add(new PrefabDefinition(
                "unit.fire",
                EntityKind.Card,
                hosts: HostKind.Unit,
                privateFields: new[]
                {
                    new PrivateFieldDefinition("hp", ViewFieldType.Number, 0m),
                    new PrivateFieldDefinition("buddy", ViewFieldType.Entity),
                },
                slots: sameName
                    ? new[] { new AssetSlotDeclaration("hp", SlotType.Number, 3m) }
                    : new[] { new AssetSlotDeclaration("power", SlotType.Number, 2m) },
                save: save ?? "test.full"))
            .Add(new PrefabDefinition(
                "unit.fire.child", EntityKind.Card, extends: "unit.fire", hosts: HostKind.Unit))
            .Add(new PrefabDefinition(
                "unit.fire.override", EntityKind.Card, extends: "unit.fire", hosts: HostKind.Unit, save: "test.other"))
            .Add(new PrefabDefinition("unit.nosave", EntityKind.Card, hosts: HostKind.Unit))
            .Add(new PrefabDefinition("legacy.manual", EntityKind.Card, hosts: HostKind.Unit, save: "test.full"))
            .Add(new PrefabDefinition(
                "fx.echo",
                EntityKind.Component,
                requiresHost: HostKind.Any,
                privateFields: new[] { new PrivateFieldDefinition("charge", ViewFieldType.Number, 0m) },
                save: "test.full"))
            .Add(new PrefabDefinition("fx.nosave", EntityKind.Component, requiresHost: HostKind.Any));

    internal static ComponentAsset ComponentTemplate(string save)
        => new(
            "fx.tpl",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: new[] { new PrivateFieldDefinition("charge", ViewFieldType.Number, 0m) },
            members: Array.Empty<ComponentMemberAsset>(),
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number) },
            save: save);

    private static IReadOnlyDictionary<string, object?> Args()
        => new Dictionary<string, object?>(StringComparer.Ordinal) { ["damage"] = 7m };

    private static SaveContext NewContext(OrchestrationRuntime runtime, JudicatorRegistry? judicators)
    {
        var factory = typeof(SaveContext)
            .GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                new[] { typeof(ISaveRuntime), typeof(JudicatorRegistry) });
        return (SaveContext)factory!.Invoke(new object?[] { runtime, judicators });
    }

    private static string Write(SaveModing moding)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Orc.Output.SaveModingJson.WriteModing(writer, moding);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
