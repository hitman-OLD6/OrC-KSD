using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Effects;
using Orc.Game.Model;
using Orc.Packages.Editing;
using Orc.Script;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R31／R32／R33 摊平期行为**：成员级抽象的强制、守卫烘进 csx（产物洁净）、
/// 模板库只读层的「仅限抽象」口子、多成员 <c>requires-host</c> 交集拒绝。
/// <para>
/// 夹具用**仓库里的真实模板库**（<c>packs/effect-templates</c>）作只读层，另建一个临时模板库承载
/// 「自定义组合模板」的反例（声明的宿主超出交集／交集为空）。
/// </para>
/// </summary>
public class EffectTemplateFlattenTests : IDisposable
{
    private const string DraftPath = "modings/demo.deployed.draft.moding.json";
    private const string DslPath = "handler-dsl/onDeployed.hdsl.json";

    private readonly string _root;
    private readonly string _pack;
    private readonly string _templateRoot;
    private readonly HandlerDslEnvironment _dsl = HandlerDslAssembly.Create();

    public EffectTemplateFlattenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-template-flatten-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        _templateRoot = Path.Combine(_root, "templates");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));
        Directory.CreateDirectory(Path.Combine(_pack, "handler-dsl"));
        Directory.CreateDirectory(Path.Combine(_templateRoot, "effects-v4"));

        // 只读层＝仓库里的真实模板库（不复制、不修改）。
        foreach (var file in Directory.EnumerateFiles(RepoTemplateDirectory, "*.component.json"))
        {
            File.Copy(file, Path.Combine(_templateRoot, "effects-v4", Path.GetFileName(file)));
        }

        WriteDsl(Dsl);
    }

    [Fact]
    public void 草案实现全部抽象成员_产物无abstract无guard且成员均有handler()
    {
        WriteDraft("template.unit.deployed.self", "unit");

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        Assert.True(result.Success, result.Describe());
        var product = AssetSchema.Read(result.ProductJson!, "product");

        // R31 第 2 条：产物无 abstract、成员均有 handler。
        Assert.False(product.Abstract);
        Assert.All(product.Members, m => Assert.False(m.Abstract));
        Assert.All(product.Members, m => Assert.NotNull(m.Handler));

        // 产物洁净（用户约束 #6）：既无 abstract 也无 guard（守卫已烘进 csx）。
        Assert.DoesNotContain("\"abstract\"", result.ProductJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"guard\"", result.ProductJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"label\"", result.ProductJson!, StringComparison.Ordinal);

        // 挂载随模板继承：产物仍然挂在信号上（否则效果永不触发）。
        var mount = Assert.Single(product.Mounts);
        Assert.Equal("engine#" + GameUpdates.UnitDeployed, mount.Target);
        Assert.Equal("onHook", mount.Member);
    }

    [Fact]
    public void 守卫代码位于作者效果体之前且按引用相等比较宿主与载荷实体()
    {
        WriteDraft("template.unit.deployed.self", "unit");

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);
        Assert.True(result.Success, result.Describe());

        var product = AssetSchema.Read(result.ProductJson!, "product");
        var script = product.Members.Single(m => m.Name == "onHook").Handler!.Script!;

        var guardIndex = script.IndexOf("ReferenceEquals((object)orcSelf, (object)view.Unit)", StringComparison.Ordinal);
        var bodyIndex = script.IndexOf("ctx.Stop();", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "self 档必须用引用相等比较宿主与载荷实体：\n" + script);
        Assert.True(bodyIndex > guardIndex, "守卫必须在作者效果体之前：\n" + script);
        Assert.Contains("return;", script[guardIndex..bodyIndex], StringComparison.Ordinal);
    }

    [Fact]
    public void 草案缺抽象成员_逐条列出成员名与所属模板()
    {
        // 不实现 onHook（成员表为空）。
        WriteDraft("template.unit.deployed.self", "unit", includeMember: false);

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("onHook", error.Message, StringComparison.Ordinal);
        Assert.Contains("template.unit.deployed.self", error.Message, StringComparison.Ordinal);
        Assert.Contains("抽象成员", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 草案重新声明abstract_仍视为未实现()
    {
        WriteDraft("template.unit.deployed.self", "unit", abstractMember: true);

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("重新声明为 abstract", StringComparison.Ordinal));
    }

    [Fact]
    public void 守卫所需字段被草案视图删掉_摊平拒绝()
    {
        WriteDraft("template.unit.deployed.self", "unit", fields: new[] { "Position" });

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("缺少字段 'Unit'", StringComparison.Ordinal));
    }

    [Fact]
    public void 草案extends模板库里的普通资产_拒绝且理由是跨包继承仅限模板()
    {
        WritePlainTemplateAsset();
        WriteDraft("template.plain", "any", includeMember: false);

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Message.Contains("跨包继承仅限模板", StringComparison.Ordinal));
    }

    [Fact]
    public void 未配置模板库时行为与今天一致_父不可解析但只报父无法展开()
    {
        WriteDraft("template.unit.deployed.self", "unit");

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, templateRoot: null);

        Assert.False(result.Success); // 模板不可用
        Assert.Contains(result.Errors, e => e.Message.Contains("请配置模板库根路径", StringComparison.Ordinal));
    }

    [Fact]
    public void 草案缺多个抽象成员_逐条列出而不是只报第一条()
    {
        WriteComboTemplate("template.combo.c", declaredHost: "unit", members:
        [
            ("onDeploy", GameUpdates.UnitDeployed, "self"),
            ("onDraw", GameUpdates.CardDrawn, "self"),
        ]);
        WriteDraft("template.combo.c", "unit", includeMember: false);

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        Assert.False(result.Success);
        var missing = result.Errors.Where(e => e.Message.Contains("抽象成员", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, missing.Length);
        Assert.Contains(missing, e => e.Message.Contains("onDeploy", StringComparison.Ordinal));
        Assert.Contains(missing, e => e.Message.Contains("onDraw", StringComparison.Ordinal));
    }

    // ── R32 第 6／7 条：自定义组合模板的 requires-host 交集 ─────────────────────

    [Fact]
    public void 声明的requires_host超出交集_拒绝并指明成员与宿主类型()
    {
        WriteComboTemplate("template.combo.a", declaredHost: "any", members:
        [
            ("onDeploy", GameUpdates.UnitDeployed, "self"),
            ("onDraw", GameUpdates.CardDrawn, "self"),
        ]);
        WriteDraft("template.combo.a", "any", memberName: "onDeploy");

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        // unit.deployed(self) ∩ card.drawn(self) ＝ Unit ∩ Any ＝ Unit，声明 any 更宽 ⇒ 拒绝。
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors, e => e.Message.Contains("永不命中", StringComparison.Ordinal));
        Assert.Contains("onDeploy", error.Message, StringComparison.Ordinal);
        Assert.Contains("order", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 交集为空的组合_拒绝并同时列出两个互斥成员()
    {
        WriteComboTemplate("template.combo.b", declaredHost: "unit", members:
        [
            ("onDeploy", GameUpdates.UnitDeployed, "self"),
            ("onCounter", GameUpdates.CounterTriggered, "self"),
        ]);
        WriteDraft("template.combo.b", "unit", memberName: "onDeploy");

        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors, e => e.Message.Contains("交集为空", StringComparison.Ordinal));
        Assert.Contains("onDeploy", error.Message, StringComparison.Ordinal);
        Assert.Contains("onCounter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 单位部署加抽到_推导为Unit可被单位卡装配()
    {
        Assert.True(SignalGuards.TryDeriveRequiresHost(
            new[]
            {
                new GuardedMember("onDeploy", GameUpdates.UnitDeployed, HandlerGuard.Self),
                new GuardedMember("onDraw", GameUpdates.CardDrawn, HandlerGuard.Self),
            },
            HostKind.Unit,
            out var host,
            out var error), error);

        Assert.Equal(HostKind.Unit, host);
    }

    [Fact]
    public void 产物可单独装载_守卫文本随产物一起预编译()
    {
        WriteDraft("template.unit.deployed.self", "unit");
        var result = ModingFlattener.Flatten(_pack, DraftPath, _dsl, _templateRoot);
        Assert.True(result.Success, result.Describe());

        var isolated = Path.Combine(_root, "isolated");
        var directory = Path.Combine(isolated, "effects-v4");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, result.ProductId + ".component.json"), result.ProductJson!);

        var runtime = GameEnginePrefabs.CreateRuntime();
        var load = AssetPackLoader.Load(
            runtime, directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator(), handlerDsl: _dsl);

        Assert.True(load.Loaded, string.Join("；", load.Errors.Select(e => $"[{e.Category}] {e.Target}：{e.Message}")));
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static string RepoTemplateDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName ?? AppContext.BaseDirectory;
            return Path.Combine(root, "packs", "effect-templates", "effects-v4");
        }
    }

    private static readonly string Dsl = """{"schemaVersion":1,"actions":[{"op":"stop"}]}""";

    private void WriteDsl(string content)
    {
        var path = Path.Combine(_pack, DslPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteDraft(
        string parentId,
        string requiresHost,
        bool includeMember = true,
        bool abstractMember = false,
        string memberName = "onHook",
        string[]? fields = null)
    {
        var viewFields = fields ?? new[] { "Unit", "Position", "Source" };
        var fieldsJson = string.Join(
            ",\n",
            viewFields.Select(static f =>
                $$"""            { "name": "{{f}}", "type": "entity", "writable": false, "required": false }"""));
        var memberJson = includeMember
            ? $$"""
                    {
                      "name": "{{memberName}}",
                      "visibility": [ "mountable" ],
                      "view": { "fields": [
            {{fieldsJson}}
                      ] },
                      {{(abstractMember ? "\"abstract\": true," : string.Empty)}}
                      "handler": {
                        "id": "demo.deployed.draft#{{memberName}}",
                        "entry": "HandleAsync",
                        "dsl": "{{DslPath}}",
                        "dslHash": "{{HandlerContent.Hash(Dsl)}}"
                      }
                    }
            """
            : string.Empty;

        Write(
            DraftPath,
            $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "moding",
                "id": "demo.deployed.draft",
                "product": { "id": "demo.deployed" },
                "extends": "{{parentId}}",
                "requires-host": "{{requiresHost}}",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [
            {{memberJson}}
                ],
                "mounts": [],
                "wraps": []
              }
            }
            """);
    }

    /// <summary>模板库里的**普通**（非 abstract）资产：用来验证「跨包继承仅限模板」。</summary>
    private void WritePlainTemplateAsset()
        => Write(
            Path.Combine(_templateRoot, "effects-v4", "template.plain.component.json"),
            """
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "template.plain",
                "authorize-moding": true,
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [],
                "mounts": [],
                "wraps": []
              }
            }
            """);

    /// <summary>自定义组合模板（多抽象成员，自定成员名）——用于 requires-host 交集反例。</summary>
    private void WriteComboTemplate(
        string id, string declaredHost, (string Member, string Signal, string Guard)[] members)
    {
        var membersJson = string.Join(
            ",\n",
            members.Select(static m =>
                $$"""
                {
                  "name": "{{m.Member}}",
                  "visibility": [ "mountable" ],
                  "view": { "fields": [] },
                  "abstract": true,
                  "guard": "{{m.Guard}}"
                }
                """));

        var mountsJson = string.Join(
            ",\n",
            members.Select(static m => $$"""{ "target": "engine#{{m.Signal}}", "member": "{{m.Member}}" }"""));

        Write(
            Path.Combine(_templateRoot, "effects-v4", id + ".component.json"),
            $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "{{id}}",
                "abstract": true,
                "requires-host": "{{declaredHost}}",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [
            {{membersJson}}
                ],
                "mounts": [
            {{mountsJson}}
                ],
                "wraps": []
              }
            }
            """);
    }

    private void Write(string relativeOrAbsolute, string content)
    {
        var path = Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(_pack, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar));
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
}
