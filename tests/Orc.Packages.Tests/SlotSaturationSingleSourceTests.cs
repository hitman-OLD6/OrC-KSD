using Orc.Core;
using Orc.Core.Model;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **S34 B**：三处结论必须来自**同一个合并谓词**——资产层（<see cref="ComponentAsset.HasUnfilledSlots"/>）、
/// 展开层（<see cref="PrefabType.HasUnfilledSlots"/>）、DTO（<see cref="PrefabSummary.IsTemplate"/>）。
/// <para>
/// 夹具覆盖**三种半边状态**：只填成员层（资产级未填）／只填资产级（成员未填）／两层全填。
/// 行为守护，不做源码字符串扫描（S14 的措辞放宽口径）。
/// </para>
/// </summary>
public class SlotSaturationSingleSourceTests : IDisposable
{
    private readonly string _root;
    private readonly string _pack;
    private readonly AssetEditor _editor;

    public SlotSaturationSingleSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-sat-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        _editor = new AssetEditor(new AssetRegistry(), new EditableRootPolicy(_root));
    }

    [Fact]
    public void 三种半边状态_资产层展开层与DTO结论一致()
    {
        var cases = new[]
        {
            // 只填资产级：成员 slot 没有 handler ⇒ 未饱和。
            (Asset: Asset("fixture.member", memberFilled: false, assetFilled: true), Unfilled: true, MemberUnfilled: true),
            // 只填成员层：资产级 damage 没有 value ⇒ 未饱和。
            (Asset: Asset("fixture.asset", memberFilled: true, assetFilled: false), Unfilled: true, MemberUnfilled: false),
            // 两层全填 ⇒ 具体资产。
            (Asset: Asset("fixture.full", memberFilled: true, assetFilled: true), Unfilled: false, MemberUnfilled: false),
        };

        foreach (var (asset, unfilled, memberUnfilled) in cases)
        {
            File.WriteAllText(
                Path.Combine(_pack, "effects-v4", asset.Id + ".component.json"),
                AssetSchema.Write(asset));

            var read = AssetSchema.Read(File.ReadAllText(
                Path.Combine(_pack, "effects-v4", asset.Id + ".component.json")), asset.Id);

            var prefab = new PrefabCatalog().Add(read.ToStructuralPrefabDefinition()).Resolve(asset.Id);
            var description = _editor.DescribePrefab(_pack, asset.Id);

            Assert.Equal(unfilled, read.HasUnfilledSlots);       // ① 装载期（资产层）
            Assert.Equal(unfilled, prefab.HasUnfilledSlots);     // ② 展开层
            Assert.Equal(unfilled, description.Prefab!.IsTemplate); // ③ DTO
            Assert.Equal(
                memberUnfilled,
                description.Members.Single(m => m.Name == "onHook").RequiresImplementation);
        }
    }

    private static ComponentAsset Asset(string id, bool memberFilled, bool assetFilled)
        => new(
            id,
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: new[]
            {
                new ComponentMemberAsset(
                    "onHook",
                    MemberVisibility.Callable,
                    ViewSpec.Empty,
                    memberFilled
                        ? new ComponentHandlerAsset("csx/v1/x", "HandleAsync", "// x", AssemblyKey: null)
                        : null,
                    Slot: MemberSlotDeclaration.Handler),
            },
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("damage", SlotType.Number, assetFilled ? 3m : null) });

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
