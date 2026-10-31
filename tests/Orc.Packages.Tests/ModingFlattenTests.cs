using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Effects;
using Orc.Game.Model;
using Orc.Packages.Editing;
using Orc.Script;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S11 摊平**（R8／R28／D9／D11）：三类成员来源分流、<c>generated</c> 来源标记、内容寻址 handler id、
/// <c>flatten --check</c> 的 <c>dslHash</c> 比对，以及「摊平的摊平不重新生成」。
/// </summary>
public class ModingFlattenTests : IDisposable
{
    private const string ParentId = "test.parent";
    private const string DraftPath = "modings/test.child.draft.moding.json";
    private const string DslPath = "handler-dsl/onDeployed.hdsl.json";

    private readonly string _root;
    private readonly string _pack;
    private readonly HandlerDslEnvironment _dsl = HandlerDslAssembly.Create();

    public ModingFlattenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-flatten-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));
        Directory.CreateDirectory(Path.Combine(_pack, "handler-dsl"));

        Write("effects-v4/test.parent.component.json", Parent);
        Write(DslPath, Dsl);
        Write(DraftPath, Draft);
    }

    [Fact]
    public void 摊平产物自包含_三类来源分流且id按内容寻址()
    {
        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl);

        Assert.True(result.Success, result.Describe());
        Assert.Equal("test.child", result.ProductId);

        var product = AssetSchema.Read(result.ProductJson!, "product");
        Assert.Null(product.Extends); // R8 第 4 条：产物不含 extends

        // 成员顺序＝父的展开表序（确定性的一部分）。
        Assert.Equal(new[] { "onIdle", "onDeployed", "onNew" }, product.Members.Select(m => m.Name));

        // ① 未修改且父有 csx 原文：文本逐字节复制、无来源标记、id 按 csx/v1 重新派生。
        var idle = product.Members.Single(m => m.Name == "onIdle").Handler!;
        Assert.Equal(ParentScript, idle.Script);
        Assert.Null(idle.Generated);
        Assert.Equal(HandlerContent.AddressOf(ParentScript, generated: false), idle.Id);
        Assert.True(idle.Id.StartsWith("csx/v1/", StringComparison.Ordinal));

        // ② 由 DSL 生成：文本 ＋ generated 块；id 按 hdsl/v1 内容寻址。
        var generated = product.Members.Single(m => m.Name == "onDeployed").Handler!;
        Assert.NotNull(generated.Script);
        Assert.Contains("ctx.Stop();", generated.Script!, StringComparison.Ordinal);
        Assert.True(generated.Id.StartsWith("hdsl/v1/", StringComparison.Ordinal));
        Assert.Equal(HandlerContent.AddressOf(generated.Script!, generated: true), generated.Id);

        var block = Assert.IsType<HandlerGeneratedBlock>(generated.Generated);
        Assert.Equal(HandlerGeneratedBlock.CurrentGenerator, block.Generator);
        Assert.Equal(_dsl.Catalog.Revision, block.CatalogRevision);
        Assert.Equal(DslPath, block.Source);
        Assert.Equal(HandlerContent.Hash(Dsl), block.DslHash);
        Assert.Equal(HandlerContent.Hash(generated.Script!), block.TextHash);

        // 物里不出现父资产的 handler id（R8 第 4 条）。
        Assert.DoesNotContain("test.parent#", result.ProductJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void 摊平是确定性的_两次产出逐字节一致()
    {
        var first = ModingFlattener.Flatten(_pack, DraftPath, _dsl);
        var second = ModingFlattener.Flatten(_pack, DraftPath, _dsl);

        Assert.True(first.Success && second.Success);
        Assert.Equal(first.ProductJson, second.ProductJson);
        Assert.Equal(first.ProductId, second.ProductId);
    }

    [Fact]
    public void 摊平的摊平_复制父的已生成文本与来源标记_不重新生成()
    {
        var first = ModingFlattener.FlattenAndWrite(_pack, DraftPath, _dsl);
        Assert.True(first.Success, first.Describe());

        // 第二层草案：只新增一个成员，其余继承第一层产物。
        Write("modings/test.grandchild.draft.moding.json", GrandchildDraft);

        // **故意不装配编译器**：若第二层重新生成，就会当场报「未装配编译器」；只复制才能成功（R28／D3 第 ③ 类）。
        var second = ModingFlattener.Flatten(_pack, "modings/test.grandchild.draft.moding.json", dsl: null);
        Assert.True(second.Success, second.Describe());

        var parentProduct = AssetSchema.Read(first.ProductJson!, "first");
        var secondProduct = AssetSchema.Read(second.ProductJson!, "second");

        var before = parentProduct.Members.Single(m => m.Name == "onDeployed").Handler!;
        var after = secondProduct.Members.Single(m => m.Name == "onDeployed").Handler!;

        Assert.Equal(before.Id, after.Id); // 沿用父的 id
        Assert.Equal(before.Script, after.Script); // 文本逐字复制
        Assert.Equal(before.Generated!.TextHash, after.Generated!.TextHash);
        Assert.Equal(before.Generated.DslHash, after.Generated.DslHash);
    }

    [Fact]
    public void 产物可单独装载_不要求父存在()
    {
        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl);
        Assert.True(result.Success, result.Describe());

        // 只把产物放进一个空包：父资产完全不注册（R8 第 1 条）。
        var isolated = Path.Combine(_root, "isolated");
        Directory.CreateDirectory(Path.Combine(isolated, "effects-v4"));
        File.WriteAllText(
            Path.Combine(isolated, "effects-v4", result.ProductId + ".component.json"), result.ProductJson!);

        var runtime = GameEnginePrefabs.CreateRuntime();
        var load = AssetPackLoader.Load(
            runtime,
            Path.Combine(isolated, "effects-v4"),
            new CSharpScriptEvaluator(),
            new CSharpSyncScriptEvaluator(),
            handlerDsl: _dsl);

        Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => $"[{e.Category}] {e.Target}：{e.Message}")));
        Assert.Equal(new[] { result.ProductId }, load.AssetIds);
    }

    [Fact]
    public void 生成物被改写_产物装载期拒绝()
    {
        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl);
        Assert.True(result.Success, result.Describe());

        // 手工改掉生成文本（保留来源标记）：textHash 与实际文本不符＝「生成物被改写」（R28 末条）。
        var tampered = result.ProductJson!.Replace("ctx.Stop();", "ctx.Stop(); // hand edit", StringComparison.Ordinal);
        Assert.NotEqual(result.ProductJson, tampered);

        var isolated = Path.Combine(_root, "tampered");
        Directory.CreateDirectory(Path.Combine(isolated, "effects-v4"));
        File.WriteAllText(
            Path.Combine(isolated, "effects-v4", result.ProductId + ".component.json"), tampered);

        var runtime = GameEnginePrefabs.CreateRuntime();
        var load = AssetPackLoader.Load(
            runtime,
            Path.Combine(isolated, "effects-v4"),
            new CSharpScriptEvaluator(),
            new CSharpSyncScriptEvaluator(),
            handlerDsl: _dsl);

        Assert.False(load.Loaded);
        Assert.Contains(load.Errors, e => e.Message.Contains("生成物被改写", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_DSL变更后报不一致并列出差异项()
    {
        var flattened = ModingFlattener.FlattenAndWrite(_pack, DraftPath, _dsl);
        Assert.True(flattened.Success, flattened.Describe());

        var upToDate = ModingFlattener.Check(_pack, DraftPath);
        Assert.True(upToDate.UpToDate, upToDate.Describe());

        // 改 DSL 但不碰草案：dslHash 与文件当前内容不符。
        Write(DslPath, Dsl.Replace("\"stop\"", "\"interrupt\"", StringComparison.Ordinal));

        var stale = ModingFlattener.Check(_pack, DraftPath);

        Assert.False(stale.UpToDate);
        var item = Assert.Single(stale.Items);
        Assert.Equal(ModingStaleReason.DslChanged, item.Reason);
        Assert.Equal("self#onDeployed", item.Target);
        Assert.Contains("DSL 已变更，请重新摊平", item.Message, StringComparison.Ordinal);
        // 差异项里能看到「即将变化的 handler id」（D9：资产 id 稳定、handler id 跟内容走）。
        Assert.Contains("hdsl/v1/", item.Message, StringComparison.Ordinal);
        Assert.Contains("DSL 已变更，请重新摊平", stale.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_父新增成员而产物未跟上_报草案落后于父()
    {
        var first = ModingFlattener.FlattenAndWrite(_pack, DraftPath, _dsl);
        Assert.True(first.Success, first.Describe());

        // 父新增一个成员（草案没同步）：摊平过的产物缺它＝「草案落后于父」（R8 第 8 条）。
        Write(
            "effects-v4/test.parent.component.json",
            Parent.Replace("\"members\": [", "\"members\": [\n" + AdditionalParentMember + ",", StringComparison.Ordinal));

        var stale = ModingFlattener.Check(_pack, DraftPath);
        Assert.False(stale.UpToDate);
        Assert.Contains(stale.Items, i => i.Reason == ModingStaleReason.ParentAhead && i.Target == "self#onLate");
    }

    [Fact]
    public void 草案id与产物id相同_摊平拒绝()
    {
        Write(DraftPath, Draft.Replace("\"product\": { \"id\": \"test.child\" },", "\"product\": { \"id\": \"test.child.draft\" },"));

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("草案 id 与产物 id 相同", StringComparison.Ordinal));
    }

    [Fact]
    public void 产物内不含任何DSL节点结构()
    {
        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl);
        Assert.True(result.Success, result.Describe());

        using var document = JsonDocument.Parse(result.ProductJson!);
        // R28 第 1 条：产物只有 csx 文本 ＋ 来源标记，没有任何 DSL 节点字段。
        var handler = document.RootElement.GetProperty("asset").GetProperty("members")
            .EnumerateArray().First(m => m.GetProperty("name").GetString() == "onDeployed")
            .GetProperty("handler");

        Assert.False(handler.TryGetProperty("dsl", out _));
        Assert.False(handler.TryGetProperty("actions", out _));
        Assert.True(handler.TryGetProperty("script", out _));
        Assert.True(handler.TryGetProperty("generated", out _));
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string ParentScript =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;";

    private const string Dsl = """{"schemaVersion":1,"actions":[{"op":"stop"}]}""";

    private const string AdditionalParentMember = """
        {
                "name": "onLate",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "test.parent#onLate",
                  "entry": "HandleAsync",
                  "script": "public Task HandleAsync(dynamic view, Context ctx, CancellationToken ct) => Task.CompletedTask;"
                }
              }
        """;

    private static readonly string Parent = $$"""
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "component",
            "id": "test.parent",
            "authorize-moding": true,
            "requires-host": "any",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onIdle",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "test.parent#onIdle",
                  "entry": "HandleAsync",
                  "script": "{{ParentScript}}"
                }
              },
              {
                "name": "onDeployed",
                "visibility": [ "callable" ],
                "view": { "fields": [ { "name": "Defense", "type": "number", "writable": true, "required": false } ] },
                "handler": {
                  "id": "test.parent#onDeployed",
                  "entry": "HandleAsync",
                  "script": "{{ParentScript}}"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;

    private static readonly string Draft = $$"""
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "test.child.draft",
            "product": { "id": "test.child" },
            "extends": "test.parent",
            "requires-host": "any",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onDeployed",
                "visibility": [ "callable" ],
                "view": { "fields": [ { "name": "Defense", "type": "number", "writable": true, "required": false } ] },
                "handler": {
                  "id": "test.child.draft#onDeployed",
                  "entry": "HandleAsync",
                  "dsl": "{{DslPath}}",
                  "dslHash": "{{HandlerContent.Hash(Dsl)}}"
                }
              },
              {
                "name": "onNew",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "test.child.draft#onNew",
                  "entry": "HandleAsync",
                  "script": "{{ParentScript}}"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;

    private static readonly string GrandchildDraft = $$"""
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "test.grandchild.draft",
            "product": { "id": "test.grandchild" },
            "extends": "test.child",
            "requires-host": "any",
            "active-when": "placed",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onGrand",
                "visibility": [ "callable" ],
                "view": { "fields": [] },
                "handler": {
                  "id": "test.grandchild.draft#onGrand",
                  "entry": "HandleAsync",
                  "script": "{{ParentScript}}"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """;
}
