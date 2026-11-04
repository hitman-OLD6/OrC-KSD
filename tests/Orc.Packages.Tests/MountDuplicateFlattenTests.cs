using Orc.Core.Model;
using Orc.Game.Model;
using Orc.Packages.Editing;
using Orc.Script;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R40（挂载／包装重复声明＝装载期拒绝）** 的摊平／装载验收：拒绝发生在合并核
/// <see cref="PrefabCatalog.Build"/>，摊平与资产装载两条路径一次覆盖（R40.5）。
/// <para>
/// 端到端行为（打出后手牌 +2）在 <c>Orc.Game.Tests</c> 的
/// <c>PrefabMountDuplicateEndToEndTests</c> 里——只有那里能看见「翻倍」这个症状。
/// </para>
/// </summary>
public class MountDuplicateFlattenTests : IDisposable
{
    private const string ParentId = "test.r40.parent";
    private const string DraftId = "test.r40.child.draft";
    private const string ProductId = "test.r40.child";
    private const string DraftPath = "modings/test.r40.child.draft.moding.json";
    private const string Played = "engine#card.played";

    private readonly string _root;
    private readonly string _pack;

    public MountDuplicateFlattenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-r40-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));
    }

    [Fact]
    public void 草案重复父的挂载_摊平拒绝且零产物写盘()
    {
        Write("effects-v4/test.r40.parent.component.json", Parent());
        Write(DraftPath, Draft(mounts: $"{{ \"target\": \"{Played}\", \"member\": \"onPlayed\" }}"));

        var result = ModingFlattener.FlattenAndWrite(_pack, DraftPath);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Equal(DraftId, error.Target);
        // 报错必须指出地址与**双方声明者**（父资产 id ＋ 本资产 id，R40.1／D50）。
        Assert.Contains(Played, error.Message, StringComparison.Ordinal);
        Assert.Contains(ParentId, error.Message, StringComparison.Ordinal);
        Assert.Contains(DraftId, error.Message, StringComparison.Ordinal);

        // 零产物写盘（R8 第 10 条／R40 验收）：失败不碰磁盘。
        Assert.False(File.Exists(Path.Combine(_pack, "effects-v4", ProductId + ".component.json")));
    }

    [Fact]
    public void 草案新增父没有的挂载_摊平成功且产物两条都在()
    {
        Write("effects-v4/test.r40.parent.component.json", Parent());
        Write(DraftPath, Draft(
            mounts: null,
            extraMembers: ExtraOnDrawn,
            extraMounts: "{ \"target\": \"engine#card.drawn\", \"member\": \"onDrawn\" }"));

        var result = ModingFlattener.Flatten(_pack, DraftPath);

        Assert.True(result.Success, result.Describe());
        var product = AssetSchema.Read(result.ProductJson!, "product");
        Assert.Equal(
            new[] { Played, "engine#card.drawn" },
            product.Mounts.Select(static m => m.Target));
        Assert.Equal(new[] { "onPlayed", "onDrawn" }, product.Mounts.Select(static m => m.Member));
    }

    [Fact]
    public void 产物JSON不含归属字段_字段集与今天一致()
    {
        Write("effects-v4/test.r40.parent.component.json", Parent());
        Write(DraftPath, Draft(mounts: null));

        var result = ModingFlattener.Flatten(_pack, DraftPath);

        Assert.True(result.Success, result.Describe());
        Assert.DoesNotContain("declaredBy", result.ProductJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("declarer", result.ProductJson!, StringComparison.OrdinalIgnoreCase);

        using var document = System.Text.Json.JsonDocument.Parse(result.ProductJson!);
        var asset = document.RootElement.GetProperty("asset");
        var mount = Assert.Single(asset.GetProperty("mounts").EnumerateArray());
        Assert.Equal(
            new[] { "member", "target" },
            mount.EnumerateObject().Select(static p => p.Name).OrderBy(static n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void 手写资产父子重复挂载_打包装载拒绝且运行期零注册()
    {
        var directory = Path.Combine(_root, "dup-pack");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "test.r40.hand.parent.component.json"), Parent());
        File.WriteAllText(
            Path.Combine(directory, "test.r40.hand.child.component.json"),
            Asset(
                id: "test.r40.hand.child",
                extends: ParentId,
                mounts: $"{{\"target\": \"{Played}\", \"member\": \"onPlayed\"}}",
                memberHandler: Handler));

        var runtime = GameEnginePrefabs.CreateRuntime();
        var before = runtime.Catalog.Definitions.Count;

        // 合并核在暂存面抛出 ⇒ 整包失败、正式运行时零注册（`AssetPackLoader` 在该处不吞异常，
        // 见 PROGRESS 的偏差记录：拒绝形态是抛出而非 `Success=false`）。
        var error = Assert.Throws<PrefabLoadException>(() => AssetPackLoader.Load(
            runtime,
            directory,
            new CSharpScriptEvaluator(),
            new CSharpSyncScriptEvaluator()));

        Assert.Equal(PrefabErrorCategories.Structure, error.Category);
        Assert.Contains(Played, error.Message, StringComparison.Ordinal);
        Assert.Contains(ParentId, error.Message, StringComparison.Ordinal);
        Assert.Contains("test.r40.hand.child", error.Message, StringComparison.Ordinal);

        // 正式运行时的目录没有被触碰（临场没注册任何新资产）。
        Assert.Equal(before, runtime.Catalog.Definitions.Count);
        Assert.DoesNotContain(runtime.Catalog.Definitions, d => d.Id.StartsWith("test.r40.", StringComparison.Ordinal));
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private const string Handler =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;";

    private const string ExtraOnDrawn = """
        {
          "name": "onDrawn",
          "visibility": [ "callable" ],
          "view": { "fields": [] },
          "handler": {
            "id": "test.r40.child.draft#onDrawn",
            "entry": "HandleAsync",
            "script": "Func<dynamic, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => Task.CompletedTask;"
          }
        }
        """;

    private static string Asset(string id, string? extends, string? mounts, string memberHandler)
    {
        var extendsLine = extends is null ? string.Empty : $"\"extends\": \"{extends}\",";
        var mountArray = mounts is null ? "[]" : "[" + mounts + "]";
        return $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "{{id}}",
                {{extendsLine}}
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [
                  {
                    "name": "onPlayed",
                    "visibility": [ "callable", "mountable" ],
                    "view": { "fields": [] },
                    "slot": { "type": "handler" },
                    "handler": {
                      "id": "{{id}}#onPlayed",
                      "entry": "HandleAsync",
                      "script": "{{memberHandler}}"
                    }
                  }
                ],
                "mounts": {{mountArray}},
                "wraps": []
              }
            }
            """;
    }

    private static string Parent() => Asset(ParentId, extends: null, mounts: $"{{ \"target\": \"{Played}\", \"member\": \"onPlayed\" }}", memberHandler: Handler);

    private static string Draft(string? mounts, string? extraMembers = null, string? extraMounts = null)
    {
        var mountEntries = new List<string>();
        if (mounts is not null)
        {
            mountEntries.Add(mounts);
        }

        if (extraMounts is not null)
        {
            mountEntries.Add(extraMounts);
        }

        var mountArray = mountEntries.Count == 0 ? "[]" : "[" + string.Join(",", mountEntries) + "]";
        var members = extraMembers is null ? string.Empty : ",\n" + extraMembers;
        return $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "moding",
                "id": "{{DraftId}}",
                "product": { "id": "{{ProductId}}" },
                "extends": "{{ParentId}}",
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [
                  {
                    "name": "onPlayed",
                    "visibility": [ "callable" ],
                    "view": { "fields": [] },
                    "handler": {
                      "id": "{{DraftId}}#onPlayed",
                      "entry": "HandleAsync",
                      "script": "{{Handler}}"
                    }
                  }{{members}}
                ],
                "mounts": {{mountArray}},
                "wraps": []
              }
            }
            """;
    }

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
}
