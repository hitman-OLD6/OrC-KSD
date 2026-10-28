using Orc.Core.Model;
using Orc.Game.Migration;
using Orc.Game.Model;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S11 验收（资产 schema 迁移）——迁移器本身：全量转换、反向自检、无旧残留、csx 正文零差异、
/// 显示名映射与实现侧常量同源（R1j 守护测试）。
/// </summary>
public class AssetSchemaMigrationTests
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

    [Fact]
    public void csx头部改写后正文逐字节不变()
    {
        const string source = "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n    view.Amount = 1;\n};\n";
        var rewritten = AssetSchemaMigrator.RewriteScriptHeader(source);

        Assert.StartsWith("Func<dynamic, Context, CancellationToken, Task> HandleAsync", rewritten, StringComparison.Ordinal);

        // 正文零差异：从入口名之后逐字节相等。
        var originalBody = source[source.IndexOf("HandleAsync", StringComparison.Ordinal)..];
        var rewrittenBody = rewritten[rewritten.IndexOf("HandleAsync", StringComparison.Ordinal)..];
        Assert.Equal(originalBody, rewrittenBody);
    }

    [Fact]
    public void csx头部改写幂等且判定器同步签名不动()
    {
        const string judge = "Func<object[], object[], object[]> Handle = (args, result) => { return result; };\n";
        Assert.Equal(judge, AssetSchemaMigrator.RewriteScriptHeader(judge));

        const string once = "Func<Orc.Game.Damage.DamageApplyView, Context, CancellationToken, Task> Handle = (v, c, t) => Task.CompletedTask;";
        var rewritten = AssetSchemaMigrator.RewriteScriptHeader(once);
        Assert.Equal(rewritten, AssetSchemaMigrator.RewriteScriptHeader(rewritten));
    }

    [Fact]
    public void 显示名映射常量集合与预制体成员集合一致_守护漂移()
    {
        // R1j：迁移器用的映射表必须与实现侧成员名常量同源——两者都来自 EngineProcesses（唯一真源）。
        // 本测试把"常量表声明的成员"与"预制体实际成员"钉成同一条断言：任一漂移＝红灯。
        var catalog = GameEnginePrefabs.CreateCatalog();
        var engineMembers = catalog.Resolve(GameEnginePrefabs.PrefabId).MemberNames.ToHashSet(StringComparer.Ordinal);

        foreach (var mapping in EngineProcesses.InjectTargets)
        {
            if (mapping.Scope == AddressScope.Engine)
            {
                Assert.True(
                    engineMembers.Contains(mapping.MemberName),
                    $"injects 映射目标 '{mapping.DisplayName}' → '{mapping.Address}' 的成员 '{mapping.MemberName}' "
                    + "不在引擎预制体成员表里（常量表与实现漂移，R1j）。");
                continue;
            }

            // host# 目标：必须在**全部**宿主类型上都存在（按当前宿主实例解析天然消歧，R1j）。
            var hostTypes = catalog.HostTypesCovering(HostKind.Any);
            Assert.NotEmpty(hostTypes);
            foreach (var hostType in hostTypes)
            {
                Assert.True(
                    hostType.HasMember(mapping.MemberName),
                    $"injects 映射目标 '{mapping.DisplayName}' → '{mapping.Address}' 的成员 '{mapping.MemberName}' "
                    + $"不在宿主类型 '{hostType.Id}' 的成员表里（R1j：host# 必须在该宿主上可解析）。");
            }
        }

        // 反向：映射表里的每个成员都必须在对应实现侧的成员名常量里（同一份常量）。
        foreach (var mapping in EngineProcesses.All)
        {
            if (mapping.Scope == AddressScope.Engine)
            {
                Assert.Contains(mapping.MemberName, EngineProcesses.MemberNames);
            }
            else
            {
                Assert.Contains(mapping.MemberName, Orc.Game.Model.CardProcesses.UnitMemberNames
                    .Concat(Orc.Game.Model.CardProcesses.OrderMemberNames)
                    .Concat(Orc.Game.Model.CardProcesses.CounterMemberNames)
                    .Append(Orc.Game.Model.CardProcesses.Targeting));
            }
        }
    }

    [Fact]
    public void 生产包不含旧格式效果库_且各包无待迁移项()
    {
        // v4-only 绊线（§23.8 第 1 项）：生产包只允许 effects-v4/*.component.json。
        // 旧格式 effects/ 与 *.prefab.json 已随旧装载链删除；本用例是**绊线**——有人把旧格式资产加回来，
        // 这里会红，避免"两套格式并存 → 装载面歧义 → 静默不装载"重演（那类故障在审查图上看起来完全正常）。
        var packs = Directory.GetDirectories(Path.Combine(RepoRoot(), "samples"), "card-pack-*")
            .OrderBy(static d => d, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(packs);

        foreach (var pack in packs)
        {
            var name = Path.GetFileName(pack);
            Assert.False(
                Directory.Exists(Path.Combine(pack, "effects")),
                $"{name} 仍含旧格式 effects/ 目录（v4-only：请迁到 effects-v4/）。");

            var report = AssetSchemaMigrator.MigratePack(pack);
            Assert.True(report.Ok, $"{name}：{string.Join("；", report.Failures)}");
            Assert.Empty(report.Assets); // 生产包已无旧格式资产 → 迁移器无可迁移项
        }
    }

    [Fact]
    public void 迁移器不硬编码显示名映射_未登记显示名被明确拒绝()
    {
        const string json = """
            {
              "schemaVersion": 3,
              "root": {
                "id": "x",
                "mainTrigger": {
                  "id": "t.main",
                  "kind": "passive",
                  "viewType": "Orc.Cards.CardEventView",
                  "hooks": [],
                  "events": [
                    { "id": "e", "entry": "HandleAsync", "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (v,c,t) => Task.CompletedTask;" }
                  ]
                },
                "otherTriggers": [],
                "injects": [ { "target": "某个不存在的触发器", "event": "e" } ]
              }
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() => AssetSchemaMigrator.Migrate(json, "x.prefab.json"));
        Assert.Contains("不在映射表里", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void oneShot迁移为one_shot_且旧字段消失()
    {
        const string json = """
            {
              "schemaVersion": 3,
              "root": {
                "id": "x.once",
                "version": 1,
                "oneShot": true,
                "mainTrigger": {
                  "id": "t.main",
                  "kind": "passive",
                  "viewType": "Orc.Cards.CardEventView",
                  "hooks": [ "card.destroyed" ],
                  "events": [ { "id": "e", "entry": "HandleAsync", "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (v,c,t) => Task.CompletedTask;" } ]
                },
                "otherTriggers": [],
                "injects": []
              }
            }
            """;

        var migrated = AssetSchemaMigrator.Migrate(json, "x.once.prefab.json");

        Assert.Contains("\"one-shot\": true", migrated.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("oneShot", migrated.Json, StringComparison.Ordinal);

        var asset = AssetSchema.Read(migrated.Json);
        Assert.True(asset.OneShot);
        Assert.Equal(1, migrated.Counters.OneShot);
    }

    [Fact]
    public void 落盘产物与内存迁移结果逐字节一致()
    {
        // v4-only 后旧格式只存在于归档（samples/_legacy）；落盘会写 <pack>/effects-v4，
        // 归档是只读历史、不应被迁移器改写——因此把该包的旧资产复制到临时包，在那里落盘并比对。
        var source = Path.Combine(RepoRoot(), "samples", "_legacy", "card-pack-space-war", "effects");
        if (!Directory.Exists(source))
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "orc-migrate-" + Guid.NewGuid().ToString("N"));
        var effects = Path.Combine(temp, "effects");
        Directory.CreateDirectory(effects);
        try
        {
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(effects, Path.GetFileName(file)));
            }

            var report = AssetSchemaMigrator.MigratePackToDisk(temp);
            Assert.True(report.Ok, string.Join("；", report.Failures));

            foreach (var asset in report.Assets)
            {
                var path = Path.Combine(report.OutputDirectory, asset.AssetId + ".component.json");
                Assert.True(File.Exists(path), $"{path} 不存在（落盘缺产物）。");
                Assert.Equal(asset.Json, File.ReadAllText(path));
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
