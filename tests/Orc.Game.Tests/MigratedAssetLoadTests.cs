using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S11 验收（产物可装载）：samples 的全量迁移产物走**新模型的装载门**
/// （全量校验 → 统一注册；csx 装载期预编译；卡包原子）。
/// </summary>
public class MigratedAssetLoadTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static IReadOnlyList<string> MigratedDirectories()
    {
        var samples = Path.Combine(RepoRoot(), "samples");
        if (!Directory.Exists(samples))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(samples, "effects-v4", SearchOption.AllDirectories)
            .OrderBy(static d => d, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void 全部迁移产物可装载_并按包原子注册()
    {
        var directories = MigratedDirectories();
        Assert.NotEmpty(directories);

        var runtime = GameEnginePrefabs.CreateRuntime();
        var loaded = 0;

        foreach (var directory in directories)
        {
            var result = AssetPackLoader.Load(
                runtime, directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator());

            Assert.True(
                result.Loaded,
                $"{directory} 装载失败（{result.Errors.Count} 项）："
                + string.Join("；", result.Errors.Select(static e => $"[{e.Category}] {e.Target}：{e.Message}")));
            loaded += result.AssetIds.Count;
        }

        Assert.Equal(20, loaded);

        // 每个资产都能展开成组件预制体（成员表静态可枚举）。
        foreach (var directory in directories)
        {
            foreach (var file in Directory.GetFiles(directory, AssetSchema.FilePattern))
            {
                var asset = AssetSchema.Read(File.ReadAllText(file), Path.GetFileName(file));
                var type = runtime.Catalog.Resolve(asset.Id);
                Assert.Equal(EntityKind.Component, type.Kind);
                Assert.Equal(asset.Members.Select(static m => m.Name), type.MemberNames);
            }
        }
    }

    [Fact]
    public void 迁移产物的成员可实例化并按声明序建边()
    {
        var runtime = GameEnginePrefabs.CreateRuntime();
        var directory = MigratedDirectories().First(d => File.Exists(Path.Combine(d, "keyword.magnetic-shield.component.json")));
        var result = AssetPackLoader.Load(runtime, directory, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator());
        Assert.True(result.Loaded);

        // 卡牌实例由类型预制体声明；用 card.unit 作宿主（requires-host: any）。
        var host = runtime.CreateEntity(CardProcesses.UnitPrefabId, "宿主卡");
        var component = runtime.ConstructComponent(host, "keyword.magnetic-shield", "强磁护盾");
        component.Assemble();

        Assert.True(component.IsAssembled);
        Assert.Contains(component.Handles, static h => h.Edge.Source == EdgeSource.FrameworkDisposal);

        // b6：该资产旧 hooks 里的 `card.destroyed` 是填充值（视图与本信号无同名字段），迁移器不再为它造挂载边——
        // 接线全在 wraps（damage-apply / unit-attack，见下）。本条原为「存在一条 Explicit 边」，
        // 那条边正是误挂的 card.destroyed（实测：卡销毁时会跑 onAttack），故改为断言它不再出现。
        Assert.DoesNotContain(component.Handles, static h => h.Edge.Source == EdgeSource.Explicit);

        // 目标成员＝engine#damage-apply（injects 的 Rewrite 迁移为 before 包装）。
        var wraps = runtime.Wraps.Entries(
            WrapTarget.ForMember(MemberAddress.Create(AddressScope.Engine, EngineProcesses.DamageApply)), WrapperPhase.Before);
        Assert.Contains(wraps, static w => w.Handler.Id == "keyword.magnetic-shield#onApply");

        // 目标成员＝engine#unit-attack（injects 的 Commit 迁移为 after 包装）。
        var after = runtime.Wraps.Entries(
            WrapTarget.ForMember(MemberAddress.Create(AddressScope.Engine, EngineProcesses.UnitAttack)), WrapperPhase.After);
        Assert.Contains(after, static w => w.Handler.Id == "keyword.magnetic-shield#onAttack");
    }

    [Fact]
    public void 单份资产损坏_整包零注册且列出全部失败项()
    {
        var source = MigratedDirectories().First(d => File.Exists(Path.Combine(d, "keyword.magnetic-shield.component.json")));
        var temp = Path.Combine(Path.GetTempPath(), "orc-assetpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            // 两份好资产 + 两份坏资产：失败项必须**收集齐**（不是首个即停），且一份都不注册。
            foreach (var name in new[] { "keyword.magnetic-shield.component.json", "keyword.sponge-armor.component.json" })
            {
                File.Copy(Path.Combine(source, name), Path.Combine(temp, name));
            }

            File.WriteAllText(Path.Combine(temp, "broken-a.component.json"), "{ not json ");
            File.WriteAllText(
                Path.Combine(temp, "broken-b.component.json"),
                """{ "schemaVersion": 3, "root": { "id": "old", "hooks": [] } }""");

            var runtime = GameEnginePrefabs.CreateRuntime();
            var before = runtime.Catalog.Definitions.Count;

            var result = AssetPackLoader.Load(runtime, temp, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator());

            Assert.False(result.Loaded);
            Assert.Empty(result.AssetIds);
            Assert.Equal(2, result.Errors.Count);
            Assert.Contains(result.Errors, static e => e.Target == "broken-a.component.json");
            Assert.Contains(result.Errors, static e => e.Target == "broken-b.component.json");
            Assert.Contains(result.Errors, static e => e.Message.Contains("旧格式", StringComparison.Ordinal));

            // 零注册残留：目录没多一份预制体，handler 表也没有半包资产。
            Assert.Equal(before, runtime.Catalog.Definitions.Count);
            Assert.False(runtime.Handlers.TryResolve("keyword.magnetic-shield#onApply", out _));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void csx编译失败_整包拒绝且分类为编译或绑定()
    {
        var temp = Path.Combine(Path.GetTempPath(), "orc-assetpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            File.WriteAllText(Path.Combine(temp, "bad-script.component.json"), """
                {
                  "schemaVersion": 4,
                  "asset": {
                    "kind": "component",
                    "id": "bad.script",
                    "requires-host": "any",
                    "active-when": "loaded",
                    "one-shot": false,
                    "private-fields": [],
                    "members": [
                      {
                        "name": "main",
                        "visibility": ["mountable"],
                        "view": { "fields": [] },
                        "handler": {
                          "id": "bad.script#main",
                          "entry": "Handle",
                          "script": "Func<dynamic, Context, CancellationToken, Task> Handle = (v, c, t) => { this is not csharp; };"
                        }
                      }
                    ],
                    "mounts": [ { "target": "engine#card.destroyed", "member": "main" } ],
                    "wraps": []
                  }
                }
                """);

            var runtime = GameEnginePrefabs.CreateRuntime();
            var result = AssetPackLoader.Load(runtime, temp, new CSharpScriptEvaluator(), new CSharpSyncScriptEvaluator());

            Assert.False(result.Loaded);
            var error = Assert.Single(result.Errors);
            Assert.Equal(ScriptErrorCategories.Compile, error.Category);
            Assert.False(runtime.Catalog.Contains("bad.script"));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
