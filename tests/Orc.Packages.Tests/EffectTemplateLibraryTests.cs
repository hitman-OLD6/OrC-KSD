using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Model;
using Orc.Packages.Editing;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// **R33／D30 模板库守护**：库是生成物，守护必须「按规则重算再逐条比对」——
/// 少一条、多一条、改名、手改任一字节都失败；重生成只能走 <c>ORC_WRITE_TEMPLATES=1</c>。
/// <para>
/// 断言里的数字（104 ＝ any 39 ＋ self 27 ＋ owner-same 19 ＋ owner-different 19）只是**此刻的实测快照**：
/// 加信号就会变，所以断言写法是「与派生结果一致」＋把数字写进失败信息供人核对。
/// </para>
/// </summary>
public class EffectTemplateLibraryTests
{
    /// <summary>模板库根（仓库内受版本控制的生成物）。</summary>
    private static string TemplateRoot => Path.Combine(FindRepoRoot(), "packs", "effect-templates");

    [Fact]
    public void 派生矩阵_39个信号_104条模板且四档条数与快照一致()
    {
        var specs = EffectTemplateLibrary.Derive();

        var derived = specs.Select(static s => s.Signal).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(39, derived.Count); // 40 − card.load（SignalTiming.Load 不出模板）
        Assert.DoesNotContain(GameUpdates.CardLoad, derived);

        Assert.Equal(39, specs.Count(s => s.Guard == HandlerGuard.Any));  // any 恒出
        Assert.Equal(27, specs.Count(s => s.Guard == HandlerGuard.Self)); // Subjects 白名单 27 条
        Assert.Equal(19, specs.Count(s => s.Guard == HandlerGuard.OwnerSame));
        Assert.Equal(19, specs.Count(s => s.Guard == HandlerGuard.OwnerDifferent));
        Assert.Equal(104, specs.Count);

        // id 形态（D30）：template.<signal>.<guard>，无 '/'（否则不能当文件名），且唯一。
        Assert.All(specs, s => Assert.Equal("template." + s.Signal + "." + HandlerGuardNames.Format(s.Guard), s.Id));
        Assert.All(specs, s => Assert.DoesNotContain('/', s.Id));
        Assert.DoesNotContain(specs.GroupBy(static s => s.Id, StringComparer.Ordinal), static g => g.Count() > 1);
        Assert.All(specs, s => Assert.True(AssetSchema.IsTemplateId(s.Id)));

        // active-when 的穷举规则（D26）：Placed ⇒ placed、Any ⇒ loaded、Load ⇒ 不出模板。
        foreach (var spec in specs)
        {
            var timing = SignalGuards.MemberOf(spec.Signal)!.SignalTiming!.Value;
            Assert.Equal(timing == SignalTiming.Placed ? ActiveWhen.Placed : ActiveWhen.Loaded, spec.ActiveWhen);
        }

        // 内核 card.placed 是唯一 Placed 信号 ⇒ 唯一 placed 模板档。
        Assert.Equal(
            new[] { "template.card.placed.any", "template.card.placed.self" },
            specs.Where(s => s.ActiveWhen == ActiveWhen.Placed).Select(static s => s.Id).OrderBy(static s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void 文案派生_label为信号中文名_非any档加括号_描述取Notes()
    {
        var specs = EffectTemplateLibrary.Derive();

        var deployedSelf = specs.Single(s => s.Id == "template.unit.deployed.self");
        Assert.Equal("部署时（本单位）", deployedSelf.Label);

        var deployedAny = specs.Single(s => s.Id == "template.unit.deployed.any");
        Assert.Equal("部署时", deployedAny.Label); // any 档按 D25「不显示」

        // card.leaving（R29 第 3 条）：标签与描述都必须含「非死亡」，且同时有 card.died 档。
        var leaving = specs.Single(s => s.Id == "template.card.leaving.any");
        Assert.Contains("非死亡", leaving.Label, StringComparison.Ordinal);
        Assert.NotNull(leaving.Description);
        Assert.Contains("非死亡", leaving.Description!, StringComparison.Ordinal);
        Assert.Contains("死亡不触发", leaving.Description!, StringComparison.Ordinal);
        Assert.NotNull(specs.Single(s => s.Id == "template.card.died.any"));

        // description 直接取 Notes（不双写一份）。
        Assert.Equal(GameSignals.Notes[GameUpdates.CardLeaving], leaving.Description);

        // 没有 Notes 的信号：description 省略（null）。
        Assert.Null(specs.Single(s => s.Id == "template.unit.deployed.any").Description);
    }

    [Fact]
    public void 每条模板的requires_host与guard推导逐条一致()
    {
        foreach (var spec in EffectTemplateLibrary.Derive())
        {
            Assert.Equal(SignalGuards.DeriveHostKind(spec.Guard, spec.Signal), spec.RequiresHost);

            // 再走一遍「声明的宿主不宽于交集」的校验：单成员模板必须恒成立。
            Assert.True(
                SignalGuards.TryDeriveRequiresHost(
                    new[] { new GuardedMember("onHook", spec.Signal, spec.Guard) }, spec.RequiresHost, out var host, out var error),
                error);
            Assert.Equal(spec.RequiresHost, host);
        }

        // 具体三例（D24 的位标志真值）：unit.* 收紧到 Unit、counter.* 收紧到 Counter、card.* 不收紧（Any）。
        Assert.Equal(HostKind.Unit, SignalGuards.DeriveHostKind(HandlerGuard.Self, GameUpdates.UnitDeployed));
        Assert.Equal(HostKind.Counter, SignalGuards.DeriveHostKind(HandlerGuard.Self, GameUpdates.CounterTriggered));
        Assert.Equal(HostKind.Any, SignalGuards.DeriveHostKind(HandlerGuard.Self, GameUpdates.CardDrawn));
        Assert.Equal(HostKind.Any, SignalGuards.DeriveHostKind(HandlerGuard.OwnerSame, GameUpdates.UnitDeployed));
    }

    [Fact]
    public void 模板资产形态_未饱和空位带守卫且不写废止字段()
    {
        var spec = EffectTemplateLibrary.Derive().Single(s => s.Id == "template.unit.deployed.self");
        var asset = EffectTemplateLibrary.BuildAsset(spec);
        var member = Assert.Single(asset.Members);

        Assert.True(asset.HasUnfilledSlots);          // 抽象性由未填空位推导（R39 裁决 4）
        Assert.Equal(HostKind.Unit, asset.RequiresHost);
        Assert.Equal("部署时（本单位）", asset.Label);

        Assert.Equal("onHook", member.Name);          // R34 第 3 条：内置模板成员名固定
        Assert.NotNull(member.Slot);                  // 1:1 迁移：slot: { type: "handler" }
        Assert.True(member.IsUnfilledSlot);
        Assert.Equal(HandlerGuard.Self, member.Guard);
        Assert.Null(member.Handler);                  // 未填：实现由子提供

        // 信号由**挂载声明**给出（不从 id 解析）：摊平/守卫都靠它。
        var mount = Assert.Single(asset.Mounts);
        Assert.Equal("engine#" + GameUpdates.UnitDeployed, mount.Target);
        Assert.Equal("onHook", mount.Member);

        // 视图＝信号的载荷视图（守卫要按字段名比较，故必须原样带上）。
        Assert.Equal(
            SignalGuards.MemberOf(GameUpdates.UnitDeployed)!.View.Fields.Select(static f => f.Name),
            member.View.Fields.Select(static f => f.Name));

        // 落盘文本：成员带 slot 与 guard，**不带**已废止的 abstract／authorize-moding。
        var json = EffectTemplateLibrary.Render(spec);
        Assert.Contains("\"slot\": {", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"handler\"", json, StringComparison.Ordinal);
        Assert.Contains("\"guard\": \"self\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"template.unit.deployed.self\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"abstract\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("authorize-moding", json, StringComparison.Ordinal);

        // 且能原样读回（schema 往返）。
        var read = AssetSchema.Read(json, "template");
        Assert.True(read.HasUnfilledSlots);
        Assert.True(Assert.Single(read.Members).IsUnfilledSlot);
        Assert.Equal(HandlerGuard.Self, Assert.Single(read.Members).Guard);
    }

    [Fact]
    public void 每条模板的成员均为handler空位_独立断言不走重算快照()
    {
        // **为什么必须独立**（R39 裁决 7「守护必须显式」）：`ExpectedFiles` 与本生成器同源，
        // 生成器漏写 `slot` 时「逐条一致」仍会全绿（重算跟着一起变绿）。这条断言直接读
        // `Derive() → BuildAsset()` 的**对象图**，不读快照文本，因此能抓住生成器的降级。
        // **已人工验证（2026-11-03，S34 A）**：临时删掉生成器的 `Slot:` 行 ⇒ 本断言在
        // `Assert.NotNull(member.Slot)` 处转红；临时改成 `Sealed: true` ⇒ 在 `Assert.False(member.Sealed)` 处转红。
        var specs = EffectTemplateLibrary.Derive();
        Assert.NotEmpty(specs);

        foreach (var spec in specs)
        {
            var asset = EffectTemplateLibrary.BuildAsset(spec);

            // 模板不得用资产级 slot 承载空洞（空洞在成员层）。
            Assert.Empty(asset.Slots);

            var member = Assert.Single(asset.Members);
            Assert.Equal(EffectTemplateSpec.MemberName, member.Name);
            Assert.NotNull(member.Slot);                       // 删掉生成器的 slot 行 ⇒ 这里转红
            Assert.Equal(SlotType.Handler, member.Slot!.Type);
            Assert.Null(member.Handler);                       // 未填 ⇒ D40 第 1 行
            Assert.True(member.IsUnfilledSlot);
            Assert.False(member.Sealed);                       // 加 sealed ⇒ 抽象永远无法解除
            Assert.True(asset.HasUnfilledSlots);               // 模板＝未饱和（可被 components 拒绝）

            // S34 C（迁移等价性）：迁移前的 `abstractMember: true` 集合＝迁移后的 `slot ∧ ¬handler` 集合，
            // 102 条**逐条**都恰为 {onHook}（1:1，不是「大致相同」）。
            Assert.Equal(
                new[] { EffectTemplateSpec.MemberName },
                asset.Members.Where(m => m.IsUnfilledSlot).Select(m => m.Name).ToArray());
        }
    }

    [Fact]
    public void 模板库文件集合与派生结果逐条一致_手改任一文件即失败()
    {
        var expected = EffectTemplateLibrary.ExpectedFiles(TemplateRoot);
        var directory = Path.Combine(TemplateRoot, EffectTemplateLibrary.AssetsDirectory);

        Assert.True(Directory.Exists(directory), $"模板库缺失：{directory}（应由 ORC_WRITE_TEMPLATES=1 的生成测试产出）。");

        var actual = Directory
            .EnumerateFiles(directory, AssetSchema.FilePattern, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToArray();
        var expectedNames = expected.Keys
            .Select(Path.GetFileName)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedNames, actual); // 少一条、多一条、改名都失败

        foreach (var (path, text) in expected)
        {
            var onDisk = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.True(
                string.Equals(text, onDisk, StringComparison.Ordinal),
                $"模板文件被手改：{Path.GetFileName(path)}（重生成只能走 ORC_WRITE_TEMPLATES=1）。");
        }
    }

    /// <summary>生成器：只在 <c>ORC_WRITE_TEMPLATES=1</c> 下写文件（与 ORC_WRITE_CATALOG／ORC_WRITE_BASELINE 同约定）。</summary>
    [Fact]
    public void 仅在ORC_WRITE_TEMPLATES下重写模板库()
    {
        if (Environment.GetEnvironmentVariable(EffectTemplateLibrary.WriteSwitch) is not "1")
        {
            return; // 常态＝空跑：生成动作只能由人主动触发。
        }

        var written = EffectTemplateLibrary.WriteAll(TemplateRoot);
        Assert.Equal(EffectTemplateLibrary.Derive().Count, written);
    }

    [Fact]
    public void 模板库不参与对局装载_整包被保留前缀拒绝且零注册()
    {
        // R33 第 3 条：把模板库放进**对局装载链**——它不会以「能装配的效果」身份溜进来。
        var runtime = GameEnginePrefabs.CreateRuntime();
        var load = AssetPackLoader.Load(
            runtime,
            Path.Combine(TemplateRoot, EffectTemplateLibrary.AssetsDirectory),
            evaluator: null);

        Assert.False(load.Loaded);
        Assert.Empty(load.AssetIds);
        // 两条结构性拒绝都在装载链上：保留前缀（D30）＋ 产物不得带未填空位（R31 修订／S32）。
        Assert.All(load.Errors, e => Assert.True(
            e.Message.Contains("保留前缀", StringComparison.Ordinal)
            || e.Message.Contains("未填空位", StringComparison.Ordinal),
            $"模板库装载只允许出现两类结构拒绝，收到：{e.Message}"));
        Assert.Contains(load.Errors, e => e.Message.Contains("保留前缀", StringComparison.Ordinal));
        Assert.Empty(runtime.Catalog.Definitions.Where(d => AssetSchema.IsTemplateId(d.Id)));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
