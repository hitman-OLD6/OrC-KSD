using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Orc.Tests.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S3／S4／S5 验收＋门禁（S7 第一组，纯内核路径——内置 B 归属 Orc.Game，这里自注册等价导出器）：
/// <list type="bullet">
/// <item>S3：ExportSave 独立入口（一次性求值、失败即抛）；null 载荷显式拒绝；三态失败文案可分辨；
/// moding 改写随之生效；每实体只求值一次；存档只读（状态不变、事件流零增长）。</item>
/// <item>S4：附件嵌入（一层、顺序保留、同 prefab 多份各自成条）；附件未声明 save ⇒ 宿主失败并报组件 id。</item>
/// <item>S5：savedEntities 可选段（缺席≠[]）；枚举三判据（无宿主∧非引擎∧非 legacy.）；schemaVersion 保持 2。</item>
/// <item>门禁：既有审查导出与基线**逐字节**相同；ExportSave 失败时 ExportAuditChainJson／QueryChain 仍成功。</item>
/// </list>
/// </summary>
public sealed class SaveExportTests
{
    private const string FullCopy = "test.full";

    // ==================== S3：ExportSave 入口 ====================

    /// <summary>端到端：宿主＋附件各得一份 moding；slots/privateFields/attachments 形态正确；schemaVersion 保持 2。</summary>
    [Fact]
    public void ExportSave_端到端_宿主与附件形态完整()
    {
        var (engine, runtime, _) = Scene();
        var host = runtime.CreateEntity("unit.fire", "fire1");
        var attachment = runtime.GrantComponent(host, "fx.echo", "echo1");
        attachment.Component.SetPrivateField("charge", 5m);
        host.SetPrivateField("hp", 1m); // 状态演化：_state.hp=1，配置 hp=3（两张表并存）

        using var doc = JsonDocument.Parse(engine.ExportSave());

        Assert.Equal(2, doc.RootElement.GetProperty("schemaVersion").GetInt32()); // 新段不升版本（D38）
        var saved = doc.RootElement.GetProperty("savedEntities");
        var entry = Assert.Single(saved.EnumerateArray().ToArray());

        Assert.Equal("fire1", entry.GetProperty("name").GetString());
        var moding = entry.GetProperty("moding");
        Assert.Equal("unit.fire", moding.GetProperty("extends").GetString()); // 结构锚点＝源预制体
        Assert.Equal(2m, moding.GetProperty("slots").GetProperty("power").GetDecimal()); // 配置值
        Assert.Equal(1m, moding.GetProperty("privateFields").GetProperty("hp").GetDecimal()); // 当前状态

        var attachments = moding.GetProperty("attachments");
        var embedded = Assert.Single(attachments.EnumerateArray().ToArray());
        Assert.Equal("fx.echo", embedded.GetProperty("templateId").GetString());
        Assert.Equal(5m, embedded.GetProperty("privateFields").GetProperty("charge").GetDecimal());
    }

    /// <summary>门户（D50）：未声明 save 的实体不进 savedEntities（快照不写该实体）；有存档实体时是数组、无实体时是 []。</summary>
    [Fact]
    public void ExportSave_门户缺席即排除_无实体时是空数组()
    {
        var (engine, runtime, _) = Scene();
        runtime.CreateEntity("unit.nosave", "plain1"); // 未声明 save：排除

        using var doc = JsonDocument.Parse(engine.ExportSave());
        var saved = doc.RootElement.GetProperty("savedEntities");
        Assert.Equal(0, saved.GetArrayLength()); // 求值了但无可存档实体 ⇒ []（缺席与此可区分）

        Assert.False(engine.ExportAuditChainJson().Contains("savedEntities", StringComparison.Ordinal)); // 默认导出无该键
    }

    /// <summary>三态失败可分辨：①服务未注册；②注册表未装配；③导出器返回 null。文案互异且指明实体。</summary>
    [Fact]
    public void ExportSave_失败三态可分辨()
    {
        // ① 服务未注册（D50：不自动兜底）。
        var scene1 = Scene(save: "missing.service", registerExporter: false);
        scene1.Runtime.CreateEntity("unit.fire", "fire1");
        var error1 = Assert.Throws<InvalidOperationException>(() => scene1.Engine.ExportSave());
        Assert.Contains("未注册", error1.Message, StringComparison.Ordinal);
        Assert.Contains("missing.service", error1.Message, StringComparison.Ordinal);

        // ② 本对局未装配判定器注册表（Judicators == null）。
        var scene2 = Scene();
        scene2.Runtime.CreateEntity("unit.fire", "fire1");
        scene2.Engine.Judicators = null;
        var error2 = Assert.Throws<InvalidOperationException>(() => scene2.Engine.ExportSave());
        Assert.Contains("Judicators == null", error2.Message, StringComparison.Ordinal);

        // ③ 导出器返回 null ⇒ 整个存档失败（Q5），不是跳过。
        var scene3 = Scene(registerExporter: false);
        scene3.Judicators.Register(FullCopy, new NullSaveExporter());
        scene3.Runtime.CreateEntity("unit.fire", "fire1");
        var error3 = Assert.Throws<InvalidOperationException>(() => scene3.Engine.ExportSave());
        Assert.Contains("返回 null", error3.Message, StringComparison.Ordinal);
    }

    /// <summary>源不在目录 ⇒ 存档失败（D51：必要依赖，明确报缺失的预制体 id）。</summary>
    [Fact]
    public void ExportSave_源不在目录_明确失败()
    {
        var (engine, runtime, judicators) = Scene(registerExporter: false);
        judicators.Register(FullCopy, new FullCopyTestExporter());
        var host = runtime.CreateEntity("unit.fire", "fire1");

        // 附件用 inline 产物（模板**未**登记进目录）：extends 指向目录外的源 ⇒ 失败。
        var template = SavePortalTests.ComponentTemplate(save: FullCopy);
        var instance = PrefabExpander.Instantiate(new InstantiationRequest
        {
            Template = template,
            Arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["damage"] = 7m },
            Mode = ExpansionMode.Inline,
            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal) { [template.Id] = template },
            InlineTable = runtime.InlinePrefabs,
        });
        runtime.ConstructComponent(host, instance.CacheKey!, "ghost"); // inline 产物可作组件装配（R39 裁决 17）

        var error = Assert.Throws<InvalidOperationException>(() => engine.ExportSave());
        Assert.Contains("不在目录", error.Message, StringComparison.Ordinal);
        Assert.Contains("fx.tpl", error.Message, StringComparison.Ordinal);
    }

    /// <summary>null 载荷被**显式拒绝**（明确异常，不是 NRE）——D53。</summary>
    [Fact]
    public void SaveExporter_null载荷_显式拒绝()
    {
        var exporter = new FullCopyTestExporter();

        var nullPayload = Assert.Throws<ArgumentException>(() => exporter.Invoke(null));
        Assert.Contains("载荷为 null", nullPayload.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => exporter.Invoke(Array.Empty<object>()));
        Assert.Throws<ArgumentException>(() => exporter.Invoke(new object[] { null, null }));
        Assert.Throws<ArgumentException>(() => exporter.Invoke(new object[] { "not-entity", new SaveContextShim() }));
    }

    /// <summary>moding 改写（RegisterReplaceWrapper）后存档结果随之改变（统一解析点，S3 验收）。</summary>
    [Fact]
    public void ExportSave_判定器moding改写_存档随之改变()
    {
        var (engine, runtime, judicators) = Scene(registerExporter: false);
        var handle = judicators.Register(FullCopy, new FullCopyTestExporter());
        judicators.RegisterReplaceWrapper(
            handle,
            args => new object[]
            {
                new SaveModing(
                    "unit.fire",
                    new Dictionary<string, object?> { ["power"] = 99m },
                    new Dictionary<string, object?>(),
                    Array.Empty<SaveAttachment>()),
            });
        runtime.CreateEntity("unit.fire", "fire1");

        using var doc = JsonDocument.Parse(engine.ExportSave());
        var moding = doc.RootElement.GetProperty("savedEntities")[0].GetProperty("moding");
        Assert.Equal(99m, moding.GetProperty("slots").GetProperty("power").GetDecimal()); // 改写后的值
        Assert.False(moding.GetProperty("privateFields").TryGetProperty("hp", out _)); // 改写覆盖了原导出结果
    }

    /// <summary>一次 ExportSave 内每个实体只求值一次（无重复求值）。</summary>
    [Fact]
    public void ExportSave_每实体只求值一次()
    {
        var (engine, runtime, judicators) = Scene(registerExporter: false);
        var counter = new CountingExporter();
        judicators.Register(FullCopy, counter);
        runtime.CreateEntity("unit.fire", "fire1");
        runtime.CreateEntity("unit.fire", "fire2");

        engine.ExportSave();

        Assert.Equal(2, counter.Calls); // 两个根实体各一次（附件经由宿主导出器，不计入）
    }

    /// <summary>存档动作只读：导出前后私有字段逐字段相同、不发射信号、事件流零增长（约束「存不得有副作用」）。</summary>
    [Fact]
    public void ExportSave_只读_状态与事件流不变()
    {
        var (engine, runtime, _) = Scene();
        var host = runtime.CreateEntity("unit.fire", "fire1");
        host.SetPrivateField("hp", 2m);
        var before = engine.RootStream.Entries.Count;
        var jsonBefore = engine.ExportSave();

        var jsonAfter = engine.ExportSave();

        Assert.Equal(jsonBefore, jsonAfter); // 幂等（只读、无副作用 ⇒ 可反复调用）
        Assert.Equal(2m, host.GetPrivateField("hp")); // 状态未被改写
        Assert.Equal(before, engine.RootStream.Entries.Count); // 事件流零增长（不造帧、不发射）
    }

    // ==================== S4：附件层 ====================

    /// <summary>附件层（D54）：组件不单独成条（savedEntities 无宿主条目）；同 prefab 多份按数组顺序各自成条。</summary>
    [Fact]
    public void ExportSave_组件不单独成条_同prefab多份按序各自成条()
    {
        var (engine, runtime, _) = Scene();
        var host = runtime.CreateEntity("unit.fire", "fire1");
        runtime.GrantComponent(host, "fx.echo", "echo1").Component.SetPrivateField("charge", 1m);
        runtime.GrantComponent(host, "fx.echo", "echo2").Component.SetPrivateField("charge", 2m);

        using var doc = JsonDocument.Parse(engine.ExportSave());
        var saved = doc.RootElement.GetProperty("savedEntities");
        var entry = Assert.Single(saved.EnumerateArray().ToArray()); // 组件不进 savedEntities（无宿主条目）
        Assert.Equal("fire1", entry.GetProperty("name").GetString());

        var attachments = entry.GetProperty("moding").GetProperty("attachments");
        var list = attachments.EnumerateArray().ToArray();
        Assert.Equal(2, list.Length); // 不按名字去重：两份各自成条
        Assert.Equal(1m, list[0].GetProperty("privateFields").GetProperty("charge").GetDecimal()); // 顺序＝身份
        Assert.Equal(2m, list[1].GetProperty("privateFields").GetProperty("charge").GetDecimal());
    }

    /// <summary>附件未声明 save ⇒ 宿主存档失败，错误列出该组件 id（Q2：不跳过）。</summary>
    [Fact]
    public void ExportSave_附件未声明save_宿主失败并报组件id()
    {
        var (engine, runtime, _) = Scene();
        var host = runtime.CreateEntity("unit.fire", "fire1");
        runtime.GrantComponent(host, "fx.nosave", "silent1");

        var error = Assert.Throws<InvalidOperationException>(() => engine.ExportSave());
        Assert.Contains("fx.nosave", error.Message, StringComparison.Ordinal);
        Assert.Contains("fire1", error.Message, StringComparison.Ordinal);
    }

    // ==================== S5：枚举判据 ====================

    /// <summary>枚举三判据（D54）：引擎实体、legacy. 合成实体、有宿主的组件不进 savedEntities。</summary>
    [Fact]
    public void ExportSave_枚举三判据_引擎与legacy与有宿主者排除()
    {
        var (engine, runtime, _) = Scene();
        runtime.CreateEntity("legacy.manual", "synthetic"); // 框架合成（legacy. 前缀）
        var host = runtime.CreateEntity("unit.fire", "fire1");
        runtime.GrantComponent(host, "fx.echo", "echo1");

        using var doc = JsonDocument.Parse(engine.ExportSave());
        var names = doc.RootElement.GetProperty("savedEntities")
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();

        Assert.Equal(new[] { "fire1" }, names); // 引擎单例恒在（不该出现）；synthetic／echo1 不出现
        Assert.True(FrameworkEntityPolicy.IsSynthetic("legacy.signal.endpoint"));
        Assert.False(FrameworkEntityPolicy.IsSynthetic("unit.fire"));
    }

    // ==================== 门禁（S7 第一组） ====================

    /// <summary>
    /// 门禁①：既有审查导出**逐字节**不变（基线＝本会话改造前落盘）——
    /// ExportAuditChainJson 与 QueryChain 两份导出均与基线零差异（可选段缺席＝默认导出无该键）。
    /// </summary>
    [Fact]
    public void 门禁_既有审查导出与基线逐字节相同()
    {
        var engine = GateScene();

        Assert.Equal(Baseline("baseline-auditchain.json"), engine.ExportAuditChainJson(), ignoreCase: false);
        Assert.Equal(
            Baseline("baseline-querychain.json"),
            engine.ExportChainJson(TriggerId.FromKey("engine#card.placed")));
    }

    /// <summary>
    /// 门禁②：人为制造「save 服务未注册」⇒ ExportSave 抛错；而 ExportAuditChainJson／QueryChain **仍成功**
    /// 且与存档失败前**逐字节相同**——存档失败不传染诊断面（D56）。
    /// <para>注：目录里登记了带 save 的预制体 ⇒ 审查导出的 entities 段本就会多一个实体（目录的事实），
    /// 所以基线比对在本用例改为「失败前后零差异」；逐字节基线门禁在上一用例（未动目录）。</para>
    /// </summary>
    [Fact]
    public void 门禁_ExportSave失败_既有导出仍成功()
    {
        var engine = GateScene();
        engine.Judicators = new JudicatorRegistry(); // 非空注册表，但服务未注册（三态之一）
        engine.Model!.Catalog.Add(new PrefabDefinition(
            "unit.fire", EntityKind.Card, hosts: HostKind.Unit, save: "missing.service"));
        engine.Model.CreateEntity("unit.fire", "fire1"); // 可存档实体在场 ⇒ ExportSave 会走到服务解析

        var auditBefore = engine.ExportAuditChainJson();
        var chainBefore = engine.ExportChainJson(TriggerId.FromKey("engine#card.placed"));

        Assert.Throws<InvalidOperationException>(() => engine.ExportSave()); // 存档失败（服务未注册）

        Assert.Equal(auditBefore, engine.ExportAuditChainJson()); // 诊断面照常、零差异
        Assert.Equal(
            chainBefore,
            engine.ExportChainJson(TriggerId.FromKey("engine#card.placed")));
    }

    // ==================== 场景 ====================

    /// <summary>门禁场景（每个用例**全新**引擎）：内核运行时＋一条订阅（与基线落盘时同构）。</summary>
    private static LogicEngine GateScene()
    {
        var engine = new LogicEngine();
        SignalTestHelpers.Subscribe(engine, Updates.CardPlaced, "挂", new List<string>());
        return engine;
    }

    private static string Baseline(string fileName)
        => File.ReadAllText(
            Path.Combine(
                FindRepoRoot(),
                "tests", "Orc.Tests", "Golden", "save-gate", fileName));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrcEngine.sln")))
        {
            dir = dir.Parent!;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (LogicEngine Engine, OrchestrationRuntime Runtime, JudicatorRegistry Judicators) Scene(
        string? save = null, bool registerExporter = true)
    {
        var catalog = SavePortalTests.Catalog(save);
        var runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
        var judicators = new JudicatorRegistry();
        if (registerExporter)
        {
            judicators.Register(FullCopy, new FullCopyTestExporter());
        }

        var engine = new LogicEngine { Model = runtime, Judicators = judicators };
        return (engine, runtime, judicators);
    }

    /// <summary>测试用完整复制导出器（与 Orc.Game 的内置 B 同构；纯内核路径没有内置 B——已知限制 C2）。</summary>
    private sealed class FullCopyTestExporter : SaveExporter
    {
        protected override SaveModing? ExportLogic(OrchestrationEntity self, SaveContext context)
            => new(
                SaveContext.ExtendsOf(self),
                context.ReadSlots(self.Type),
                context.ReadPrivateFields(self),
                context.Runtime.ComponentsOf(self)
                    .Select(attachment => context.ExportAttachment(attachment.Component))
                    .ToArray());
    }

    private sealed class NullSaveExporter : SaveExporter
    {
        protected override SaveModing? ExportLogic(OrchestrationEntity self, SaveContext context) => null;
    }

    private sealed class CountingExporter : SaveExporter
    {
        public int Calls { get; private set; }

        protected override SaveModing? ExportLogic(OrchestrationEntity self, SaveContext context)
        {
            Calls++;
            return new SaveModing(
                SaveContext.ExtendsOf(self),
                context.ReadSlots(self.Type),
                context.ReadPrivateFields(self),
                Array.Empty<SaveAttachment>());
        }
    }

    /// <summary>载荷契约测试专用占位（非 SaveContext 类型）。</summary>
    private sealed class SaveContextShim
    {
    }
}
