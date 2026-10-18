using Orc.Core;
using Orc.Game;
using Orc.Game.Judicators;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 判定器机制装配测试（J1）：对局加载时加载（装配期注册段＋卡加载前就绪）／对局内可达面（解析/调用/moding）／
/// 未注册 fail-fast（经对局面）／跨对局隔离。
/// 对应验收：③（装配侧）④（就绪）⑤（对局内 moding）⑧（装配后可达）。
/// </summary>
public class JudicatorAssemblyTests
{
    /// <summary>测试用判定器名（受控常量；测试专用——防散落字面）。</summary>
    private const string ProbeName = "test.assembly.probe";

    /// <summary>统一形态探测判定器（测试夹具）：构造注入主逻辑（单一处理单元）。</summary>
    private sealed class ProbeJudicator : Judicator
    {
        private readonly Func<object[]?, object[]?> _logic;

        public ProbeJudicator(Func<object[]?, object[]?> logic) => _logic = logic;

        public override object[]? Invoke(object[]? args) => _logic(args);
    }

    private static Match CreateMatch(Action<JudicatorRegistry>? assembly = null)
        => new(
            GameTestData.CreateDeck(),
            GameTestData.CreateDeck(),
            GameTestData.CreateDefinitions(),
            seed: 42,
            judicatorAssembly: assembly,
            packageActivator: TestHost.Activator);

    // ---------- 装配闭环：注册段→对局内可达（解析/调用）；注册所得＝解析所得 ----------

    [Fact]
    public async Task Registered_Judicators_Are_Reachable_And_Resolvable_After_Initialize()
    {
        JudicatorRegistration? captured = null;
        JudicatorRegistry? seenRegistry = null;
        var match = CreateMatch(registry =>
        {
            seenRegistry = registry;
            captured = registry.Register(ProbeName, new ProbeJudicator(_ => new object[] { "装配期注册" }));
        });

        await match.Initialize();

        // 对局内可达面：注册表实例＝装配段所见实例（同一实例——对局级唯一）
        Assert.Same(seenRegistry, match.Judicators);

        // 装配期已注册判定器：按名解析/调用成功；解析所得＝注册所得（同一可寻址锚）
        var resolved = match.Judicators.Resolve(ProbeName);
        Assert.Same(captured, resolved);
        Assert.Equal(new object[] { "装配期注册" }, match.Judicators.Invoke(ProbeName, null));
    }

    [Fact]
    public async Task Registration_Occurs_Before_Card_Loads()
    {
        UpdateRecorder? recorder = null;
        var updatesAtRegistration = -1;
        var cardLoadsAtRegistration = -1;
        var match = CreateMatch(registry =>
        {
            // 装配期受控观测点：注册段执行时，洗切/卡加载（及一切更新）尚未发生
            updatesAtRegistration = recorder!.Types.Count;
            cardLoadsAtRegistration = recorder.Types.Count(t => t == GameUpdates.CardLoad);
            registry.Register(ProbeName, new ProbeJudicator(_ => new object[] { true }));
        });
        recorder = new UpdateRecorder(match.Engine);

        await match.Initialize();

        Assert.Equal(0, updatesAtRegistration);   // 注册先于洗切/加载段（此时尚无任何更新）
        Assert.Equal(0, cardLoadsAtRegistration); // 注册先于卡加载（无 card.load）
        Assert.Contains(GameUpdates.DeckShuffled, recorder!.Types); // 对照：洗切随后发生
        Assert.Contains(GameUpdates.CardLoad, recorder.Types);      // 对照：卡加载随后发生
    }

    // ---------- 门禁与 fail-fast：准备态拒绝访问；未注册引用 fail-fast ----------

    [Fact]
    public async Task Registry_Access_Requires_InProgress_State()
    {
        var match = CreateMatch();
        Assert.Throws<InvalidOperationException>(() => match.Judicators); // 准备态：门禁拒绝

        await match.Initialize();
        Assert.NotNull(match.Judicators); // 进行态：可达
    }

    [Fact]
    public async Task Unregistered_Reference_Fails_Fast_Through_Match()
    {
        var match = CreateMatch();
        await match.Initialize();

        Assert.Throws<KeyNotFoundException>(() => match.Judicators.Resolve("test.assembly.missing"));
        Assert.Throws<KeyNotFoundException>(() => match.Judicators.Invoke("test.assembly.missing", null));
    }

    // ---------- 对局内 moding：全局生效（按名＋句柄同变、同回退）；跨对局隔离与宽容 ----------

    [Fact]
    public async Task Moding_Through_Match_Takes_Effect_Globally()
    {
        var match = CreateMatch(registry => registry.Register(ProbeName, new ProbeJudicator(_ => new object[] { "默认" })));
        await match.Initialize();

        var handle = match.Judicators.Resolve(ProbeName);
        var m = match.Judicators.RegisterModing(handle, _ => new object[] { "改写" });
        Assert.NotNull(m);

        Assert.Equal(new object[] { "改写" }, match.Judicators.Invoke(ProbeName, null)); // 按名解析的调用
        Assert.Equal(new object[] { "改写" }, handle.Invoke(null));                      // 持有句柄的调用

        Assert.True(match.Judicators.UnregisterModing(m!));
        Assert.Equal(new object[] { "默认" }, match.Judicators.Invoke(ProbeName, null)); // 注销回退
    }

    [Fact]
    public async Task Matches_Are_Isolated_And_Cross_Match_Handles_Are_Tolerated()
    {
        var matchA = CreateMatch(registry => registry.Register(ProbeName, new ProbeJudicator(_ => new object[] { "A" })));
        var matchB = CreateMatch(registry => registry.Register(ProbeName, new ProbeJudicator(_ => new object[] { "B" })));
        await matchA.Initialize();
        await matchB.Initialize();

        var handleA = matchA.Judicators.Resolve(ProbeName);
        var m = matchA.Judicators.RegisterModing(handleA, _ => new object[] { "A改写" });
        Assert.NotNull(m);

        Assert.Equal(new object[] { "A改写" }, matchA.Judicators.Invoke(ProbeName, null)); // 对局 A：moding 生效
        Assert.Equal(new object[] { "B" }, matchB.Judicators.Invoke(ProbeName, null));     // 对局 B：不受影响（跨对局隔离）

        // 跨对局句柄宽容：向 B 注册 A 的句柄 moding＝无操作、不抛错
        Assert.Null(matchB.Judicators.RegisterModing(handleA, _ => new object[] { "不该执行" }));
        Assert.Equal(new object[] { "B" }, matchB.Judicators.Invoke(ProbeName, null));
    }

    [Fact]
    public async Task KeywordRuleOverrideIsAModingEntryAndUnregisterRestoresTheBaseline()
    {
        // L2：词条规则覆盖（combat.range 等 5 条）不再内联在 CombatJudicatorInvoker，
        // 而是判定器注册段声明的一条 moding——栈语义（最后者胜）、注销回退到「基线 + 词条覆盖」。
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var probe = match.Judicators.RegisterModing(
            match.Judicators.Resolve(JudicatorNames.CombatRange), _ => new object[] { false });

        var attacker = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], CommandTestKit.InfantryId, 1);
        var defender = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[1], CommandTestKit.InfantryId, 1);
        var targetSlot = defender.GetData<Orc.Game.Cards.UnitStateData>().Position!;

        bool RangeAvailable() => (bool)match.Judicators
            .Resolve(JudicatorNames.CombatRange)
            .Invoke(new object[] { attacker, targetSlot })![0];

        Assert.False(RangeAvailable()); // 自定义 moding 最后注册 → 胜出
        Assert.True(match.Judicators.UnregisterModing(probe!));
        Assert.True(RangeAvailable());  // 注销回退到内置「基线 + 词条覆盖」moding
    }
}
