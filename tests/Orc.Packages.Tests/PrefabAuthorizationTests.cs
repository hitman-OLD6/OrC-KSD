using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S11 授权接缝**（R2）：资产级开关（缺省关闭）＋ 成员级 <c>sealed</c>，未授权即装载期拒绝；
/// <c>framework.</c> 前缀恒 sealed；引擎内部层间继承（Kernel／Game）**整段豁免**、既有覆写逐条不变。
/// </summary>
public class PrefabAuthorizationTests
{
    [Fact]
    public void 父未打开开关_成员覆写即拒绝且含成员名与父id()
    {
        var catalog = Catalog(parentAuthorizes: false, childOverrides: true, sealedMember: false);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));

        Assert.Equal(PrefabErrorCategories.Structure, exception.Category);
        Assert.Contains("onDeployed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("parent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("authorize-moding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 父打开开关_覆写成功且整段替换()
    {
        var catalog = Catalog(parentAuthorizes: true, childOverrides: true, sealedMember: false);

        var resolved = catalog.Resolve("child");

        var member = Assert.Single(resolved.Members);
        Assert.Equal("child", member.DeclaredBy); // 整段替换：声明者是子
        Assert.Equal(1, member.OwnEdges.Count);
    }

    [Fact]
    public void 成员级sealed_即使父打开开关也拒绝()
    {
        var catalog = Catalog(parentAuthorizes: true, childOverrides: true, sealedMember: true);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));

        Assert.Contains("onDeployed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sealed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void framework前缀恒sealed_不依赖成员级字段()
    {
        var parent = Definition("parent", authorizes: true, member: Member("framework.dispose", sealedMember: false, declaredBy: "parent"));
        var child = Definition(
            "child",
            authorizes: false,
            Member("framework.dispose", sealedMember: false, declaredBy: "child"),
            extends: "parent");

        var catalog = new PrefabCatalog().Add(parent).Add(child);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));

        Assert.Contains("framework.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("framework.dispose", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 引擎层间继承豁免_既有预制体逐条展开不变()
    {
        // engine ← engine.game、base ← unit/order/counter/hq 的覆写**不受授权约束**（R2 第 5、6 条）：
        // 这些预制体都没打开 authorize-moding，却必须照旧展开。
        var catalog = GameEnginePrefabs.CreateCatalog();

        var engine = catalog.Resolve("engine.game");
        Assert.Contains("engine", engine.Chain);
        Assert.False(engine.AuthorizesModing);

        foreach (var definition in CardProcesses.Definitions)
        {
            var type = catalog.Resolve(definition.Id);
            Assert.NotEmpty(type.Chain);
        }
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static PrefabCatalog Catalog(bool parentAuthorizes, bool childOverrides, bool sealedMember)
    {
        var parent = Definition("parent", parentAuthorizes, Member("onDeployed", sealedMember, declaredBy: "parent"));
        var child = Definition(
            "child",
            authorizes: false,
            Member(
                childOverrides ? "onDeployed" : "onOther",
                sealedMember: false,
                declaredBy: "child"),
            extends: "parent");

        return new PrefabCatalog().Add(parent).Add(child);
    }

    private static PrefabDefinition Definition(
        string id, bool authorizes, MemberDefinition member, string? extends = null)
        => new(
            id,
            EntityKind.Component,
            extends: extends,
            layer: PrefabLayer.Asset,
            requiresHost: HostKind.Any,
            members: new[] { member },
            authorizesModing: authorizes);

    private static MemberDefinition Member(string name, bool sealedMember, string declaredBy)
        => new(
            name,
            ViewSpec.Empty,
            MemberVisibility.Callable,
            new[] { new OwnedEdge((_, _, _) => Task.CompletedTask) },
            layer: PrefabLayer.Asset,
            declaredBy: declaredBy,
            sealedMember: sealedMember);
}
