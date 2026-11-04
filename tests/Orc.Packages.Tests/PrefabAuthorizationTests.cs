using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S32／R39 裁决 5**：覆写判据＝「链上声明了成员级 slot ∧ 未 sealed」——**可覆写性只由声明决定**，
/// 与实现状态无关；资产级总闸（authorize-moding）已废止。
/// <c>framework.</c> 前缀恒 sealed；引擎内部层间继承（Kernel／Game）**整段豁免**、既有覆写逐条不变。
/// </summary>
public class PrefabAuthorizationTests
{
    [Fact]
    public void 父成员不是slot_覆写即拒绝且含成员名与父id()
    {
        var catalog = Catalog(parentDeclaresSlot: false, childOverrides: true, sealedMember: false);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));

        Assert.Equal(PrefabErrorCategories.Structure, exception.Category);
        Assert.Contains("onDeployed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("parent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("不是空位", exception.Message, StringComparison.Ordinal);
        // 旧开关已不存在：不得再提示「请打开 authorize-moding」。
        Assert.DoesNotContain("authorize-moding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 父声明slot_覆写成功且整段替换_产物保留slot()
    {
        var catalog = Catalog(parentDeclaresSlot: true, childOverrides: true, sealedMember: false);

        var resolved = catalog.Resolve("child");

        var member = Assert.Single(resolved.Members);
        Assert.Equal("child", member.DeclaredBy); // 整段替换：声明者是子
        Assert.Equal(1, member.OwnEdges.Count);
        Assert.NotNull(member.Slot);              // 填一次不消灭空位（D39）
    }

    [Fact]
    public void slot已sealed_子覆写拒绝且含成员名与父id()
    {
        var catalog = Catalog(parentDeclaresSlot: true, childOverrides: true, sealedMember: true);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));

        Assert.Contains("onDeployed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("parent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sealed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void framework前缀恒sealed_不依赖slot声明()
    {
        var parent = Definition("parent", Member("framework.dispose", sealedMember: false, declaredBy: "parent"));
        var child = Definition(
            "child",
            Member("framework.dispose", sealedMember: false, declaredBy: "child"),
            extends: "parent");

        var catalog = new PrefabCatalog().Add(parent).Add(child);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));

        Assert.Contains("framework.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("framework.dispose", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sealed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 同一份声明slot与handler并存_不被报错且对下游可再填()
    {
        // D40 判定表第 2 行：`slot ∧ handler` ＝ 已填但可再填。
        var parent = Definition("parent", Member("onDeployed", sealedMember: false, declaredBy: "parent", declaresSlot: true));
        var middle = Definition(
            "middle",
            Member("onDeployed", sealedMember: false, declaredBy: "middle", declaresSlot: false),
            extends: "parent");
        var leaf = Definition(
            "leaf",
            Member("onDeployed", sealedMember: false, declaredBy: "leaf", declaresSlot: false),
            extends: "middle");

        var resolved = new PrefabCatalog().Add(parent).Add(middle).Add(leaf).Resolve("leaf");

        var member = Assert.Single(resolved.Members);
        Assert.Equal("leaf", member.DeclaredBy);
        Assert.NotNull(member.Slot); // 第二层覆写后 slot 仍在 ⇒ 第三层仍可再填
    }

    [Fact]
    public void 纯编排成员不是空位_不可覆写且不使资产未饱和()
    {
        // D40 判定表第 4 行：`¬slot ∧ ¬handler` 仍是合法形态（无自有边），不是「未填空位」。
        var parent = new PrefabDefinition(
            "parent",
            EntityKind.Component,
            layer: PrefabLayer.Asset,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition("onPassive", ViewSpec.Empty, MemberVisibility.Callable),
            });
        var child = new PrefabDefinition(
            "child",
            EntityKind.Component,
            extends: "parent",
            layer: PrefabLayer.Asset,
            requiresHost: HostKind.Any,
            members: new[]
            {
                new MemberDefinition(
                    "onPassive", ViewSpec.Empty, MemberVisibility.Callable,
                    new[] { new OwnedEdge((_, _, _) => Task.CompletedTask) }),
            });

        var resolved = new PrefabCatalog().Add(parent).Resolve("parent");
        Assert.False(resolved.HasUnfilledSlots); // 纯编排成员不是空洞

        var error = Assert.Throws<PrefabLoadException>(
            () => new PrefabCatalog().Add(parent).Add(child).Resolve("child"));
        Assert.Contains("不是空位", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 资产级slot改类型_沿链拒绝()
    {
        var parent = Definition(
            "parent", Member("onDeployed", false, "parent"), slots: new[] { Slot("damage", SlotType.Number) });
        var child = Definition(
            "child", Member("onOther", false, "child"), extends: "parent",
            slots: new[] { Slot("damage", SlotType.String) });

        var catalog = new PrefabCatalog().Add(parent).Add(child);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));
        Assert.Contains("damage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("改变了类型", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 资产级slot已sealed_子再填拒绝()
    {
        var parent = Definition(
            "parent", Member("onDeployed", false, "parent"),
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number, 3m, isSealed: true) });
        var child = Definition(
            "child", Member("onOther", false, "child"), extends: "parent",
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number, 9m) });

        var catalog = new PrefabCatalog().Add(parent).Add(child);

        var exception = Assert.Throws<PrefabLoadException>(() => catalog.Resolve("child"));
        Assert.Contains("damage", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sealed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 引擎层间继承豁免_既有预制体逐条展开不变()
    {
        // engine ← engine.game、base ← unit/order/counter/hq 的覆写**不受 slot 约束**（R2 第 5、6 条）。
        var catalog = GameEnginePrefabs.CreateCatalog();

        var engine = catalog.Resolve("engine.game");
        Assert.Contains("engine", engine.Chain);

        foreach (var definition in CardProcesses.Definitions)
        {
            var type = catalog.Resolve(definition.Id);
            Assert.NotEmpty(type.Chain);
        }
    }

    // ── 夹具 ────────────────────────────────────────────────────────────────

    private static PrefabCatalog Catalog(bool parentDeclaresSlot, bool childOverrides, bool sealedMember)
    {
        var parent = Definition(
            "parent", Member("onDeployed", sealedMember, "parent", declaresSlot: parentDeclaresSlot));
        var child = Definition(
            "child",
            Member(
                childOverrides ? "onDeployed" : "onOther",
                sealedMember: false,
                declaredBy: "child",
                declaresSlot: false),
            extends: "parent");

        return new PrefabCatalog().Add(parent).Add(child);
    }

    private static PrefabDefinition Definition(
        string id,
        MemberDefinition member,
        string? extends = null,
        IReadOnlyList<AssetSlotDeclaration>? slots = null)
        => new(
            id,
            EntityKind.Component,
            extends: extends,
            layer: PrefabLayer.Asset,
            requiresHost: HostKind.Any,
            members: new[] { member },
            slots: slots);

    private static MemberDefinition Member(
        string name, bool sealedMember, string declaredBy, bool declaresSlot = false)
        => new(
            name,
            ViewSpec.Empty,
            MemberVisibility.Callable,
            new[] { new OwnedEdge((_, _, _) => Task.CompletedTask) },
            layer: PrefabLayer.Asset,
            declaredBy: declaredBy,
            sealedMember: sealedMember,
            slot: declaresSlot ? MemberSlotDeclaration.Handler : null,
            hasHandler: true);

    private static AssetSlotDeclaration Slot(string name, SlotType type)
        => new(name, type);
}
