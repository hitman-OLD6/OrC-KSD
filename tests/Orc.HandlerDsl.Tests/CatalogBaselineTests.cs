using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>
/// **节点目录基线**（S9／D12，复用既有 <c>ContractFreezeTests</c> 的模式，不新造机制）：
/// 把当前扫描出的目录记成 <c>Baseline/handler-dsl-catalog.json</c>，逐条比对**节点名／引脚名／可选性／类型**。
/// <para>
/// 比对规则（CI 口径）：
/// <list type="bullet">
/// <item><b>新增放行</b>：新节点／新引脚不需要改基线（能力增长只递增 <c>catalogRevision</c>，D1／D12）；</item>
/// <item><b>删除／改名／类型变更／可选→必填</b>＝破坏性变更：必须同时改基线，**并且**升 <c>schemaVersion</c>；</item>
/// <item><b>改了基线却没升 <c>schemaVersion</c></b> ＝另一条断言失败（把 S1 的两类变更表变成可执行检查）。</item>
/// </list>
/// </para>
/// <para>
/// 基线只经 <c>ORC_WRITE_BASELINE=1</c> 的生成测试重写（与 <c>tests/Orc.Game.Tests</c> 的契约基线同一约定）；
/// <c>ORC_REGENERATE_BASELINE=1</c> 只用于「人工确认要跳过比对」，本测试在该开关下**判失败**以防假绿。
/// </para>
/// </summary>
public class CatalogBaselineTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string BaselinePath => Path.Combine(Fixtures.RepoRoot, "tests", "Orc.HandlerDsl.Tests", "Baseline", "handler-dsl-catalog.json");

    private static string BaselineRelativePath => "tests/Orc.HandlerDsl.Tests/Baseline/handler-dsl-catalog.json";

    [Fact]
    public void 目录基线未被破坏性变更()
    {
        if (Environment.GetEnvironmentVariable("ORC_REGENERATE_BASELINE") == "1")
        {
            Assert.Fail(
                "ORC_REGENERATE_BASELINE=1 —— 目录基线门被要求跳过比对，测试判失败以防假绿。"
                + "若确需重写基线，请用 ORC_WRITE_BASELINE=1 运行本文件的生成器；"
                + "本测试永不重写基线文件（基线是受版本控制的资产）。");
        }

        Assert.True(
            File.Exists(BaselinePath),
            $"目录基线缺失：{BaselinePath}（应由 ORC_WRITE_BASELINE=1 的生成测试产出）。");

        var baseline = ReadBaseline(File.ReadAllText(BaselinePath));
        var actual = CaptureNodes();

        var changes = CollectBreakingChanges(baseline.Nodes, actual.Nodes);

        Assert.True(
            changes.Count == 0,
            "节点目录发生破坏性变更（新增是允许的，不需要改基线）：\n"
            + string.Join("\n", changes)
            + "\n若确属有意：用 ORC_WRITE_BASELINE=1 重写基线，并同时升 schemaVersion（SPEC §1／§16）。");
    }

    /// <summary>
    /// **改基线而未升 <c>schemaVersion</c> ⇒ 失败**（D12 的可执行检查）。
    /// <para>
    /// 判据用 git：①工作区基线 vs <c>HEAD</c>（提交前）；②<c>HEAD</c> vs <c>HEAD~1</c>（提交后）。
    /// 只对**破坏性**的基线改动要求升版：仅新增节点/引脚（或只是刷新 <c>catalogRevision</c>）属增量，
    /// 按 S1 的两类表只需递增 <c>catalogRevision</c>，不该被这条门误伤。
    /// git 不可用／浅克隆（取不到历史）＝**跳过**并说明——那是环境限制，不是「基线没被改」的证明。
    /// </para>
    /// </summary>
    [Fact]
    public void 基线被改动_必须升schemaVersion()
    {
        if (!File.Exists(BaselinePath))
        {
            return; // 基线缺失由「目录基线未被破坏性变更」报（本测试只守版本闸门）。
        }

        var current = ReadBaseline(File.ReadAllText(BaselinePath));

        var headText = GitShow("HEAD", BaselineRelativePath);
        if (headText is null)
        {
            return; // 不是 git 仓库／文件尚未入库：无从比对。
        }

        var head = ReadBaseline(headText);
        if (headText != File.ReadAllText(BaselinePath))
        {
            AssertVersionBumped(head, current, "目录基线在工作区被改动");
            return;
        }

        var parentText = GitShow("HEAD~1", BaselineRelativePath);
        if (parentText is null)
        {
            return; // 首次提交／浅克隆：没有可比的上一个版本。
        }

        if (parentText != headText)
        {
            AssertVersionBumped(ReadBaseline(parentText), head, "上一提交改动了目录基线");
        }
    }

    private static void AssertVersionBumped(BaselineDocument before, BaselineDocument after, string what)
    {
        if (CollectBreakingChanges(before.Nodes, after.Nodes).Count == 0)
        {
            return; // 仅增量（新增节点／引脚，或只是刷新 catalogRevision）：不需要升 schemaVersion。
        }

        Assert.True(
            before.SchemaVersion != after.SchemaVersion,
            $"{what}（含破坏性变更），但 schemaVersion 仍是 {after.SchemaVersion}"
            + "（D12：破坏性目录变更必须同时升 schemaVersion，否则它会静默滑过基线门）。");
    }

    /// <summary>
    /// 逐项比对两份基线，返回**破坏性**差异（空＝只增不减，或完全一致）。
    /// <para>
    /// 语义务必与 S1 的两类表一致：<b>新增</b>节点／引脚放行（只递增 <c>catalogRevision</c>）；
    /// <b>删除／改名／类型变更／可选→必填／取值集或子类型集收窄</b>＝破坏。
    /// </para>
    /// </summary>
    private static List<string> CollectBreakingChanges(
        IReadOnlyList<BaselineNode> expected, IReadOnlyList<BaselineNode> actual)
    {
        var changes = new List<string>();

        foreach (var expectedNode in expected)
        {
            var node = actual.FirstOrDefault(n => string.Equals(n.Name, expectedNode.Name, StringComparison.Ordinal));
            if (node is null)
            {
                changes.Add($"删除／改名节点 '{expectedNode.Name}'");
                continue;
            }

            foreach (var expectedPin in expectedNode.Pins)
            {
                var pin = node.Pins.FirstOrDefault(p => string.Equals(p.Name, expectedPin.Name, StringComparison.Ordinal));
                if (pin is null)
                {
                    changes.Add($"节点 '{expectedNode.Name}' 删除／改名引脚 '{expectedPin.Name}'");
                    continue;
                }

                if (!string.Equals(pin.Type, expectedPin.Type, StringComparison.Ordinal))
                {
                    changes.Add($"节点 '{expectedNode.Name}' 的引脚 '{expectedPin.Name}' 类型 {expectedPin.Type} → {pin.Type}");
                }

                // 只有「可选 → 必填」是破坏（旧 DSL 少连一条现在必填的引脚就会装载失败）；
                // 必填 → 可选是放宽（S1 的右列不含它）。
                if (!expectedPin.Required && pin.Required)
                {
                    changes.Add($"节点 '{expectedNode.Name}' 的引脚 '{expectedPin.Name}' 可选 → 必填");
                }

                // 取值集／子类型集的收窄是破坏；放宽是增量。两侧集合都做包含判定，方向只看「旧的是否仍被包含」。
                if (!IsSubset(expectedPin.Enum, pin.Enum))
                {
                    changes.Add($"节点 '{expectedNode.Name}' 的引脚 '{expectedPin.Name}' 取值集收窄");
                }

                if (!IsSubset(expectedPin.Subtypes, pin.Subtypes))
                {
                    changes.Add($"节点 '{expectedNode.Name}' 的引脚 '{expectedPin.Name}' 子类型集收窄");
                }
            }
        }

        return changes;
    }

    // ── 生成（唯一写入口） ────────────────────────────────────────────────────

    /// <summary>基线生成器：只在 <c>ORC_WRITE_BASELINE=1</c> 下写文件（与既有契约基线同一约定）。</summary>
    [Fact]
    public void 仅在ORC_WRITE_BASELINE下重写基线()
    {
        if (Environment.GetEnvironmentVariable("ORC_WRITE_BASELINE") != "1")
        {
            return; // 常规运行：只读不写。
        }

        Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        File.WriteAllText(BaselinePath, Serialize(CaptureNodes()), Utf8NoBom);
    }

    // ── 采集与序列化 ──────────────────────────────────────────────────────────

    private sealed record BaselineDocument(int SchemaVersion, int CatalogRevision, IReadOnlyList<BaselineNode> Nodes);    private sealed record BaselineNode(string Name, IReadOnlyList<BaselinePin> Pins);

    private sealed record BaselinePin(
        string Name, string Type, bool Required, IReadOnlyList<string> Enum, IReadOnlyList<string> Subtypes);

    /// <summary>
    /// 导出**完整目录快照**到契约包（仅在 <c>ORC_WRITE_CATALOG=1</c> 时写盘）。
    /// <para>
    /// 存在的理由：vectors 里的 <c>catalog</c> 段一度是手写的，与真实扫描悄悄漂移——
    /// 曾把 <c>DamageAsync</c> 的首参误标成隐式宿主、把 <c>FetchFromDeckAsync</c> 写成不存在的
    /// <c>FetchAsync</c>，于是向量一路绿灯而真实链路是断的。快照从扫描结果导出，才不会再骗人。
    /// </para>
    /// </summary>
    [Fact]
    public void 目录快照_可导出到契约包()
    {
        if (Environment.GetEnvironmentVariable("ORC_WRITE_CATALOG") is not "1")
        {
            return;
        }

        var scan = HandlerDslCatalogScanner.Scan(typeof(Orc.Game.Effects.EffectRuntime).Assembly);
        Assert.True(scan.Success && scan.Catalog is not null, string.Join("；", scan.Errors));

        var path = Path.Combine(Fixtures.RepoRoot, "contracts", "handler-dsl", "v1", "catalog.snapshot.json");
        File.WriteAllText(path, HandlerDslCatalogJson.Serialize(scan.Catalog!), new UTF8Encoding(false));
        Assert.True(File.Exists(path), $"快照未写出：{path}");
    }

    private static BaselineDocument CaptureNodes()
    {
        var scan = HandlerDslCatalogScanner.Scan(typeof(Orc.Game.Effects.EffectRuntime).Assembly);
        Assert.True(scan.Success && scan.Catalog is not null, string.Join("；", scan.Errors));

        var nodes = scan.Catalog!.Nodes
            .OrderBy(static n => n.Name, StringComparer.Ordinal)
            .Select(node => new BaselineNode(
                node.Name,
                node.Inputs.Concat(node.Outputs)
                    .Select(pin => new BaselinePin(
                        pin.Name,
                        BaseName(pin.Type),
                        pin.Required,
                        pin.EnumValues.OrderBy(static v => v, StringComparer.Ordinal).ToArray(),
                        SubtypesOf(pin.Type).OrderBy(static v => v, StringComparer.Ordinal).ToArray()))
                    .ToArray()))
            .ToArray();

        return new BaselineDocument(HandlerDslContract.SchemaVersion, scan.Catalog.Revision, nodes);
    }

    private static string Serialize(BaselineDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteNumber("catalogRevision", document.CatalogRevision);
            writer.WriteStartArray("nodes");
            foreach (var node in document.Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("name", node.Name);
                writer.WriteStartArray("pins");
                foreach (var pin in node.Pins)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", pin.Name);
                    writer.WriteString("type", pin.Type);
                    writer.WriteBoolean("required", pin.Required);
                    if (pin.Enum.Count > 0)
                    {
                        writer.WriteStartArray("enum");
                        foreach (var value in pin.Enum)
                        {
                            writer.WriteStringValue(value);
                        }

                        writer.WriteEndArray();
                    }

                    if (pin.Subtypes.Count > 0)
                    {
                        writer.WriteStartArray("subtypes");
                        foreach (var value in pin.Subtypes)
                        {
                            writer.WriteStringValue(value);
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static BaselineDocument ReadBaseline(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var nodes = new List<BaselineNode>();
        foreach (var nodeElement in root.GetProperty("nodes").EnumerateArray())
        {
            var pins = new List<BaselinePin>();
            foreach (var pinElement in nodeElement.GetProperty("pins").EnumerateArray())
            {
                pins.Add(new BaselinePin(
                    pinElement.GetProperty("name").GetString()!,
                    pinElement.GetProperty("type").GetString()!,
                    pinElement.TryGetProperty("required", out var required) && required.GetBoolean(),
                    Strings(pinElement, "enum"),
                    Strings(pinElement, "subtypes")));
            }

            nodes.Add(new BaselineNode(nodeElement.GetProperty("name").GetString()!, pins));
        }

        return new BaselineDocument(
            root.GetProperty("schemaVersion").GetInt32(),
            root.TryGetProperty("catalogRevision", out var revision) ? revision.GetInt32() : 0,
            nodes);
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string field)
        => element.TryGetProperty(field, out var array)
            ? array.EnumerateArray().Select(static v => v.GetString()!).ToArray()
            : Array.Empty<string>();

    /// <summary>旧集合是否仍是新集合的子集（不是＝收窄＝破坏；多出来的取值＝放宽＝增量）。</summary>
    private static bool IsSubset(IReadOnlyList<string> oldValues, IReadOnlyList<string> newValues)
        => oldValues.All(v => newValues.Contains(v, StringComparer.Ordinal));

    /// <summary>引脚类型的规范基名（不含子类型后缀；子类型单列一项，便于逐项比对）。</summary>
    private static string BaseName(PinType type) => type.Kind switch
    {
        PinTypeKind.Entity => "entity",
        PinTypeKind.Number => "number",
        PinTypeKind.String => "string",
        PinTypeKind.Boolean => "boolean",
        PinTypeKind.List => $"list<{BaseName(type.Element!)}>",
        _ => throw new InvalidOperationException("未定义的引脚类型种类。"),
    };

    private static IReadOnlyList<string> SubtypesOf(PinType type)
        => type.IsList ? type.Element!.Subtypes : type.Subtypes;

    /// <summary>取 git 里的文件内容（不可用＝null；不让环境问题变成假红）。</summary>
    private static string? GitShow(string revision, string relativePath)
    {
        try
        {
            var start = new ProcessStartInfo("git", new[] { "show", $"{revision}:{relativePath}" })
            {
                WorkingDirectory = Fixtures.RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // 没有 git：跳过历史比对（不假绿：本测试只守「改基线未升版」这一条）。
        }
    }
}
