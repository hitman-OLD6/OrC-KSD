using Orc.Core;
using Orc.Packages;
using Xunit;
using static Orc.Packages.Tests.AssetLoadTestKit;

namespace Orc.Packages.Tests;

/// <summary>
/// S5 验收（分层）：lock 集合 → 注册面取版本 → Kahn 分层（第 N 层只依赖 ≤N-1 层）→ 层内局部 ID 字典序；
/// 环依赖报错含环路径；内含资产不作为独立拓扑节点；R15 的双向依赖约束。
/// </summary>
public class LoadListPlannerTests
{
    [Fact]
    public void 分层满足第N层只依赖小于N的层()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a");
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);
        var c = Register(registry, "p", "c", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);
        var d = Register(registry, "p", "d");

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", a, b, c, d)]);

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Issues);
        var list = plan.Formal;
        Assert.Equal(3, list.LayerCount);
        Assert.Equal(new[] { "a", "d" }, list.Layers[0].Items.Select(item => item.LocalId));
        Assert.Equal(new[] { "b" }, list.Layers[1].Items.Select(item => item.LocalId));
        Assert.Equal(new[] { "c" }, list.Layers[2].Items.Select(item => item.LocalId));

        // 逐条核对「第 N 层只依赖 ≤N-1 层」（依赖检测与分层在同一次计算中完成）。
        var layerOf = list.Layers
            .SelectMany(layer => layer.Items.Select(item => (item.AppId, layer.Index)))
            .ToDictionary(pair => pair.AppId, pair => pair.Index);
        foreach (var item in list.Items)
        {
            var layer = layerOf[item.AppId];
            foreach (var reference in item.Asset.Metadata.DependsOn)
            {
                Assert.True(layerOf[reference.AppId] < layer, $"{item.AppId} 的依赖 {reference.AppId} 未落在更低的层。");
            }
        }
    }

    [Fact]
    public void 层内按局部ID字典序()
    {
        var registry = new AssetRegistry();
        var zulu = Register(registry, "p", "zulu");
        var alpha = Register(registry, "p", "alpha");
        var mike = Register(registry, "p", "mike");

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", zulu, alpha, mike)]);

        Assert.Equal(["alpha", "mike", "zulu"], plan.Formal.Layers[0].Items.Select(item => item.LocalId));
    }

    [Fact]
    public void 环依赖报错并输出环路径()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a", dependsOn: [Depends(AssetKind.Card, "pack/p/b")]);
        var b = Register(registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/c")]);
        var c = Register(registry, "p", "c", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", a, b, c)]);

        Assert.False(plan.IsValid);
        var cycle = Assert.Single(plan.Issues, issue => issue.Code == AssetLoadIssueCode.Cycle);
        Assert.True(cycle.IsFatal);
        Assert.Equal(["pack/p/a", "pack/p/b", "pack/p/c", "pack/p/a"], cycle.Path);
        Assert.Contains("pack/p/a", cycle.Message, StringComparison.Ordinal);
        Assert.Contains("→", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 自依赖也判为环()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a", dependsOn: [Depends(AssetKind.Card, "pack/p/a")]);

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", a)]);

        Assert.False(plan.IsValid);
        var cycle = Assert.Single(plan.Issues, issue => issue.Code == AssetLoadIssueCode.Cycle);
        Assert.Equal(["pack/p/a", "pack/p/a"], cycle.Path);
    }

    [Fact]
    public void 内含资产不参与拓扑_随宿主同层()
    {
        var registry = new AssetRegistry();
        var effect = Register(
            registry, "p", "fx", AssetKind.Effect, sourcePath: "assets/fx.prefab.json");
        var trigger = Register(
            registry, "p", "fx-onPlay", AssetKind.Trigger,
            anchor: "onPlay", sourcePath: "assets/fx.prefab.json");
        var handler = Register(
            registry, "p", "fx-onPlay-effect", AssetKind.Handler,
            anchor: "onPlay.effect", sourcePath: "assets/fx.prefab.json");
        var rifle = Register(registry, "p", "rifle", dependsOn: [Depends(AssetKind.Effect, "pack/p/fx")]);

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", effect, trigger, handler, rifle)]);

        Assert.True(plan.IsValid);
        var list = plan.Formal;

        // 内含资产不是独立节点：节点数＝effect ＋ rifle，层数＝2（内含资产不抬高任何层）。
        Assert.Equal(2, list.Count);
        Assert.Equal(2, list.LayerCount);
        Assert.Equal(2, list.EmbeddedCount);
        Assert.DoesNotContain(list.Items, item => item.AssetGuid == trigger.AssetGuid);

        var host = Assert.Single(list.Layers[0].Items);
        Assert.Equal("fx", host.LocalId);
        Assert.Equal([trigger.AssetGuid, handler.AssetGuid], host.Embedded.Select(asset => asset.AssetGuid));
        Assert.Same(host, list.Find(trigger.AssetGuid)); // 内含资产可经宿主定位
        Assert.Equal("rifle", Assert.Single(list.Layers[1].Items).LocalId);
    }

    [Fact]
    public void 依赖内含资产时连到宿主()
    {
        var registry = new AssetRegistry();
        var effect = Register(registry, "p", "fx", AssetKind.Effect, sourcePath: "assets/fx.prefab.json");
        var trigger = Register(
            registry, "p", "fx-onPlay", AssetKind.Trigger,
            anchor: "onPlay", sourcePath: "assets/fx.prefab.json");
        var listener = Register(
            registry, "p", "listener",
            dependsOn: [Depends(AssetKind.Trigger, "pack/p/fx-onPlay")]);

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", effect, trigger, listener)]);

        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.Formal.LayerCount);
        Assert.Equal("fx", Assert.Single(plan.Formal.Layers[0].Items).LocalId);
        Assert.Equal("listener", Assert.Single(plan.Formal.Layers[1].Items).LocalId);
    }

    [Fact]
    public void 宿主不在加载集合时内含资产进报告且不参与拓扑()
    {
        var registry = new AssetRegistry();
        var orphanTrigger = Register(
            registry, "p", "lost-onPlay", AssetKind.Trigger,
            anchor: "onPlay", sourcePath: "assets/lost.prefab.json");
        var solo = Register(registry, "p", "solo");

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", orphanTrigger, solo)]);

        Assert.True(plan.IsValid); // 资产级问题不整段失败
        var issue = Assert.Single(plan.Issues, item => item.Code == AssetLoadIssueCode.OrphanEmbedded);
        Assert.Equal("pack/p/lost-onPlay", issue.AppId);
        Assert.Equal(["solo"], plan.Formal.Items.Select(item => item.LocalId));
    }

    [Fact]
    public void 正式资产依赖测试资产判非法()
    {
        var registry = new AssetRegistry();
        var testCard = Register(registry, "p", "t", domain: AssetDomain.Test);
        var formalCard = Register(
            registry, "p", "a", dependsOn: [Depends(AssetKind.Card, "pack/p/t")]);

        var plan = LoadListPlanner.Plan(registry, [
            FormalLock("p", formalCard),
            TestLock("p", testCard),
        ]);

        Assert.True(plan.IsValid); // 资产级拒绝，不整段失败
        var issue = Assert.Single(plan.Issues, item => item.Code == AssetLoadIssueCode.FormalDependsOnTest);
        Assert.Equal("pack/p/a", issue.AppId);
        Assert.Empty(plan.Formal.Items);
        Assert.Equal(["t"], plan.Test.Items.Select(item => item.LocalId));
    }

    [Fact]
    public void 测试资产依赖的正式资产未入正式lock时只判该测试资产非法()
    {
        var registry = new AssetRegistry();
        var unlocked = Register(registry, "p", "base"); // 注册了，但不在正式 lock
        var blocked = Register(
            registry, "p", "t-blocked", domain: AssetDomain.Test,
            dependsOn: [Depends(AssetKind.Card, "pack/p/" + unlocked.LocalId)]);
        var lockedBase = Register(registry, "p", "base2");
        var ok = Register(
            registry, "p", "t-ok", domain: AssetDomain.Test,
            dependsOn: [Depends(AssetKind.Card, "pack/p/base2")]);

        var plan = LoadListPlanner.Plan(registry, [
            FormalLock("p", lockedBase),
            TestLock("p", blocked, ok),
        ]);

        Assert.True(plan.IsValid); // 不整段失败
        var issue = Assert.Single(plan.Issues, item => item.Code == AssetLoadIssueCode.TestDependsOnUnlockedFormal);
        Assert.Equal("pack/p/t-blocked", issue.AppId);
        Assert.Equal(AssetDomain.Test, issue.Domain);

        Assert.Equal(["base2"], plan.Formal.Items.Select(item => item.LocalId));
        Assert.Equal(["t-ok"], plan.Test.Items.Select(item => item.LocalId));
    }

    [Fact]
    public void 依赖版本约束不符只报告_以lock为准()
    {
        var registry = new AssetRegistry();
        var a = Register(registry, "p", "a", version: "2.0.0");
        var b = Register(
            registry, "p", "b", dependsOn: [Depends(AssetKind.Card, "pack/p/a", "1.*.*")]);

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", a, b)]);

        Assert.True(plan.IsValid);
        var issue = Assert.Single(plan.Issues, item => item.Code == AssetLoadIssueCode.DependencyVersionMismatch);
        Assert.Equal("pack/p/b", issue.AppId);
        Assert.Equal(2, plan.Formal.Count); // 仍按 lock 装载
    }

    [Fact]
    public void 依赖缺失时该资产判非法并连带依赖者()
    {
        var registry = new AssetRegistry();
        var broken = Register(
            registry, "p", "broken", dependsOn: [Depends(AssetKind.Card, "pack/p/nothing")]);
        var dependent = Register(
            registry, "p", "dependent", dependsOn: [Depends(AssetKind.Card, "pack/p/broken")]);
        var healthy = Register(registry, "p", "healthy");

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", broken, dependent, healthy)]);

        Assert.True(plan.IsValid);
        Assert.Contains(plan.Issues, issue => issue.Code == AssetLoadIssueCode.DependencyMissing);
        Assert.Contains(plan.Issues, issue => issue.Code == AssetLoadIssueCode.DependencyInvalid);
        Assert.Equal(["healthy"], plan.Formal.Items.Select(item => item.LocalId));
    }

    [Fact]
    public void 未注册的锁定版本不参与加载()
    {
        var registry = new AssetRegistry();
        var phantom = new AssetLockEntry(
            Guid.Parse("00000000-0000-4000-8000-00000000dead"),
            AssetKind.Card,
            "pack/p/phantom",
            AssetVersion.Parse("1.0.0"));

        var plan = LoadListPlanner.Plan(registry, [
            AssetLock.Create("p", AssetDomain.Formal, [phantom]),
        ]);

        var issue = Assert.Single(plan.Issues, item => item.Code == AssetLoadIssueCode.Unregistered);
        Assert.Equal("pack/p/phantom", issue.AppId);
        Assert.Equal(0, plan.Formal.Count);
    }

    [Fact]
    public void 同域lock冲突为结构性失败()
    {
        var registry = new AssetRegistry();
        var v1 = Register(registry, "p", "a", version: "1.0.0");
        var v2 = Register(registry, "p", "a", version: "1.1.0");

        var plan = LoadListPlanner.Plan(registry, [
            FormalLock("p", v1),
            FormalLock("q", v2),
        ]);

        Assert.False(plan.IsValid);
        var conflict = Assert.Single(plan.Issues, item => item.Code == AssetLoadIssueCode.LockConflict);
        Assert.Equal("pack/p/a", conflict.AppId);
        Assert.Contains("1.0.0", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("1.1.0", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 跨域注册不混入本域列表()
    {
        var registry = new AssetRegistry();
        var formal = Register(registry, "p", "a");
        var testOnly = Register(registry, "p", "b", domain: AssetDomain.Test);

        // 正式 lock 误引测试域注册的 GUID ⇒ 域不符，剔除。
        var plan = LoadListPlanner.Plan(registry, [
            AssetLock.Create("p", AssetDomain.Formal, [Entry(formal), Entry(testOnly)]),
        ]);

        Assert.Contains(plan.Issues, issue => issue.Code == AssetLoadIssueCode.DomainMismatch);
        Assert.Equal(["a"], plan.Formal.Items.Select(item => item.LocalId));
    }

    [Fact]
    public void 声明冲突双方同处加载集合判非法()
    {
        var registry = new AssetRegistry();
        var a = RegisterWithConflict(registry, "p", "a", Conflict(AssetKind.Card, "pack/p/b"));
        var b = Register(registry, "p", "b");

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", a, b)]);

        Assert.False(plan.IsValid);
        var issue = Assert.Single(plan.Issues, item => item.Code == AssetLoadIssueCode.DeclaredConflict);
        Assert.True(issue.IsFatal);
        Assert.Contains("：a@", issue.Message, StringComparison.Ordinal);
        Assert.Contains("：b@", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 声明冲突仅一方在集合不触发()
    {
        var registry = new AssetRegistry();
        var a = RegisterWithConflict(registry, "p", "a", Conflict(AssetKind.Card, "pack/p/b"));
        Register(registry, "p", "b");

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", a)]);

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void 声明冲突版本约束不满足不触发()
    {
        var registry = new AssetRegistry();
        var a = RegisterWithConflict(
            registry, "p", "a", Conflict(AssetKind.Card, "pack/p/b", "2.*.*"));
        var b = Register(registry, "p", "b", version: "1.0.0");

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", a, b)]);

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void 声明冲突按域判定_测试不污染正式()
    {
        var registry = new AssetRegistry();
        var formal = Register(registry, "p", "a");
        var testAsset = RegisterWithConflict(
            AssetDomain.Test, registry, "p", "b", Conflict(AssetKind.Card, "pack/p/a"));

        var plan = LoadListPlanner.Plan(registry, [FormalLock("p", formal), TestLock("p", testAsset)]);

        Assert.True(plan.IsValid);
        Assert.DoesNotContain(plan.Issues, item => item.Code == AssetLoadIssueCode.DeclaredConflict);
    }

    private static AssetReference Conflict(AssetKind kind, string appId, string constraint = "1.*.*")
        => new(kind, appId, constraint);

    private static AssetRegistration RegisterWithConflict(
        AssetRegistry registry,
        string packageId,
        string localId,
        params AssetReference[] conflictsWith)
        => RegisterWithConflict(AssetDomain.Formal, registry, packageId, localId, conflictsWith);

    private static AssetRegistration RegisterWithConflict(
        AssetDomain domain,
        AssetRegistry registry,
        string packageId,
        string localId,
        params AssetReference[] conflictsWith)
    {
        var version = AssetVersion.Parse("1.0.0");
        var packageGuid = PackageGuidOf(packageId);
        var registration = new AssetRegistration(
            new AssetMetadata(
                AssetIdentity.DeriveAssetGuid(packageGuid, AssetKind.Card, localId, version),
                AssetKind.Card,
                localId,
                version,
                Anchor: null,
                Loader: null,
                DependsOn: [],
                ConflictsWith: conflictsWith),
            domain,
            packageId,
            AssetAppId.Compose(packageId, localId),
            AssetMetadataSource.Sidecar,
            Card: null,
            Prefab: null,
            SourcePath: null)
        {
            PackageGuid = packageGuid,
        };

        registry.Register(registration);
        return registration;
    }
}
