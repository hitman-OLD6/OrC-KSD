using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// X4 moding（逻辑替换）机制测试（S7）。
/// 约定（流程编排禁令的测试惯例）：moding 逻辑与 handler 本体一致＝单一处理单元（读取/计算/数据改写/记录/判定）；
/// 禁止流程编排（调用其它流程/触发器、串联多个处理单元、组织多步骤序列）——本文件全部用例均以单一处理单元形态示范。
/// 覆盖：①装配期注册（基类模板＋子类代表场景）；②运行时注册/注销（最后者胜、回退、中间项）；
/// ③纯替换（原逻辑不执行·对照观测）；④无效句柄与幂等（宽容、不抛错）；⑤条目撤销＝moding 随之失效（避免悬空）；
/// ⑥快照语义（进行中不打断、下一次解析点生效、机制静默）；⑦构造期项可寻址（InitialRegistrations）；
/// ⑧用途代表场景（流程内置处理器屏蔽替换）；⑨异常隔离（沿用既有语义）。
/// </summary>
public class TriggerWrapperTests
{
    private static Task Nop(CounterView view, Context ctx, CancellationToken ct) => Task.CompletedTask;

    // ---------- 代表场景①：基类模板＋子类 moding（验收①） ----------

    /// <summary>基类模板：装配期注册模板 handler 并持有其注册句柄（供子类 moding）。</summary>
    private class TemplateTrigger : Trigger<CounterView>
    {
        protected readonly List<string> Trace;

        protected readonly TriggerRegistration TemplateHandler;

        public TemplateTrigger(List<string> trace)
            : base(name: "模板触发器")
        {
            Trace = trace;
            TemplateHandler = Register("模板处理", OnTemplate);
        }

        protected virtual Task OnTemplate(CounterView view, Context ctx, CancellationToken ct)
        {
            Trace.Add("template");
            view.Count += 1;
            return Task.CompletedTask;
        }
    }

    /// <summary>子类：装配期（构造）经基类持有句柄注册 moding，替换模板逻辑。</summary>
    private sealed class SubclassTrigger : TemplateTrigger
    {
        public TriggerWrapperRegistration ModingHandle { get; }

        public SubclassTrigger(List<string> trace)
            : base(trace)
        {
            ModingHandle = RegisterReplaceWrapper(TemplateHandler, OnModing)!;
        }

        private Task OnModing(CounterView view, Context ctx, CancellationToken ct)
        {
            Trace.Add("moding");
            view.Count += 10;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Template_Subclass_Moding_Replaces_Template_Logic()
    {
        var engine = new LogicEngine();

        // 对照：纯模板（无 moding）——模板逻辑执行
        var traceTemplate = new List<string>();
        var templateOnly = new TemplateTrigger(traceTemplate);
        await templateOnly.InvokeAsync(engine);
        Assert.Equal(new[] { "template" }, traceTemplate);

        // 子类 moding：装配期经基类持有句柄注册——替换模板逻辑
        var traceSubclass = new List<string>();
        var subclass = new SubclassTrigger(traceSubclass);
        await subclass.InvokeAsync(engine);
        Assert.Equal(new[] { "moding" }, traceSubclass); // 仅 moding 逻辑（模板逻辑无痕）

        // 装配期注册的 moding 亦可被任意持有句柄者运行时注销（句柄即权限）→ 回退模板逻辑
        Assert.True(subclass.UnregisterWrapper(subclass.ModingHandle));
        await subclass.InvokeAsync(engine);
        Assert.Equal(new[] { "moding", "template" }, traceSubclass);
    }

    // ---------- 运行时注册/注销：最后者胜、回退、重注册入栈顶（验收②） ----------

    [Fact]
    public async Task Runtime_Register_Last_Wins_Unregister_Falls_Back()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "替换语义");
        var target = trigger.Register("目标", (v, c, t) => { trace.Add("原"); return Task.CompletedTask; });

        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "原" }, trace); // 无 moding：原逻辑

        var m1 = trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("m1"); return Task.CompletedTask; });
        Assert.NotNull(m1);
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "原", "m1" }, trace); // 注册 m1：替换生效（对下一次执行立即生效）

        var m2 = trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("m2"); return Task.CompletedTask; });
        Assert.NotNull(m2);
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "原", "m1", "m2" }, trace); // 最后者胜（m1 不执行）

        Assert.True(trigger.UnregisterWrapper(m2!));
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "原", "m1", "m2", "m1" }, trace); // 注销 m2：回退 m1

        Assert.True(trigger.UnregisterWrapper(m1!));
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "原", "m1", "m2", "m1", "原" }, trace); // 全部注销：回退原逻辑

        var m3 = trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("m3"); return Task.CompletedTask; });
        Assert.NotNull(m3);
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "原", "m1", "m2", "m1", "原", "m3" }, trace); // 注销后重新注册：按新注册序入栈顶
    }

    [Fact]
    public async Task Unregister_Middle_Item_Falls_Back_To_Remaining_Last()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "栈语义");
        var target = trigger.Register("目标", (v, c, t) => { trace.Add("原"); return Task.CompletedTask; });

        var m1 = trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("m1"); return Task.CompletedTask; })!;
        var m2 = trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("m2"); return Task.CompletedTask; })!;
        var m3 = trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("m3"); return Task.CompletedTask; })!;

        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "m3" }, trace); // 最后者胜（m1、m2 完全不执行）

        Assert.True(trigger.UnregisterWrapper(m1)); // 注销中间项（栈底）
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "m3", "m3" }, trace); // 解析＝剩余项中最后注册者

        Assert.True(trigger.UnregisterWrapper(m3));
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "m3", "m3", "m2" }, trace); // 回退 m2

        Assert.True(trigger.UnregisterWrapper(m2));
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "m3", "m3", "m2", "原" }, trace); // 全部注销＝回退原逻辑
    }

    // ---------- 纯替换：原逻辑不执行（验收③；对照观测） ----------

    [Fact]
    public async Task Moding_Is_Pure_Replacement_Original_Logic_Not_Executed()
    {
        var engine = new LogicEngine();
        var originalRuns = 0;
        var modingRuns = 0;

        var trigger = new Trigger<CounterView>(name: "纯替换");
        var target = trigger.Register("目标", (v, c, t) => { originalRuns++; return Task.CompletedTask; });
        var m = trigger.RegisterReplaceWrapper(target, (v, c, t) => { modingRuns++; return Task.CompletedTask; });
        Assert.NotNull(m);

        await trigger.InvokeAsync(engine);

        Assert.Equal(0, originalRuns); // moding 生效时原逻辑不执行（对照观测：原逻辑无痕）
        Assert.Equal(1, modingRuns);   // 仅 moding 逻辑运行
    }

    // ---------- 无效句柄：幂等无操作、不抛错、不生效 ----------

    [Fact]
    public async Task RegisterReplaceWrapper_On_Invalid_Handle_Fails_Fast()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();

        var t1 = new Trigger<CounterView>(name: "T1");
        var targetA = t1.Register("A", (v, c, t) => { trace.Add("t1-A原逻辑"); return Task.CompletedTask; });

        var t2 = new Trigger<CounterView>(name: "T2");
        t2.Register("B", (v, c, t) => { trace.Add("t2-B原逻辑"); return Task.CompletedTask; });

        // ① 跨触发器句柄：无操作、不抛错、不生效（对 t1 的 A 无影响）
        Assert.Throws<ArgumentException>(() => t2.RegisterReplaceWrapper(targetA, (v, c, t) => { trace.Add("不该执行-跨触发器"); return Task.CompletedTask; }));

        await t1.InvokeAsync(engine);
        Assert.Equal(new[] { "t1-A原逻辑" }, trace); // A 仍原逻辑

        // ② 已撤销条目的句柄：无操作、不抛错、不生效
        Assert.True(t1.Unregister(targetA));
        Assert.Throws<ArgumentException>(() => t1.RegisterReplaceWrapper(targetA, (v, c, t) => { trace.Add("不该执行-已撤销"); return Task.CompletedTask; }));

        // 行为核对：t1 已无条目（空执行）；t2 的 B 不受跨触发器注册影响
        await t1.InvokeAsync(engine);
        await t2.InvokeAsync(engine);
        Assert.Equal(new[] { "t1-A原逻辑", "t2-B原逻辑" }, trace);
    }

    [Fact]
    public async Task UnregisterWrapper_Is_Idempotent_And_Handle_Scoped()
    {
        var engine = new LogicEngine();
        var trigger = new Trigger<CounterView>(name: "注销幂等");
        var target = trigger.Register("目标", Nop);

        var m = trigger.RegisterReplaceWrapper(target, Nop);
        Assert.NotNull(m);

        Assert.True(trigger.UnregisterWrapper(m!));
        Assert.False(trigger.UnregisterWrapper(m!)); // 重复注销：无操作、不抛错

        var m2 = trigger.RegisterReplaceWrapper(target, Nop)!;
        var other = new Trigger<CounterView>(name: "他者");
        Assert.False(other.UnregisterWrapper(m2)); // 句柄不属：无操作、不抛错

        // 目标注册项撤销后其 moding 随之失效：注销＝无操作
        Assert.True(trigger.Unregister(target));
        Assert.False(trigger.UnregisterWrapper(m2));
        await trigger.InvokeAsync(engine); // 空执行（无条目、无异常）
    }

    // ---------- 条目撤销＝moding 随之失效（避免悬空） ----------

    [Fact]
    public async Task Entry_Unregister_Invalidates_Its_Modings_No_Dangling()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "撤销语义");
        TriggerRegistration? target = null;

        trigger.Register("先行", (v, c, t) =>
        {
            trace.Add("先行");
            trigger.Unregister(target!); // 执行中撤销「目标」条目（既有快照语义：不影响本轮已开始的迭代）
            return Task.CompletedTask;
        });
        target = trigger.Register("目标", (v, c, t) => { trace.Add("目标原逻辑"); return Task.CompletedTask; });
        trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("目标moding"); return Task.CompletedTask; });

        await trigger.InvokeAsync(engine);

        // 既有快照语义：本轮已开始的迭代不因撤销改变（「目标」仍在快照中执行）；
        // moding 随条目撤销失效 → 回退原逻辑（不悬空：撤销后其替换逻辑不再被选用）
        Assert.Equal(new[] { "先行", "目标原逻辑" }, trace);

        // 后续轮：目标条目不再参与；向已撤销句柄再注册 moding＝无操作（不生效）
        Assert.Throws<ArgumentException>(() => trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("幽灵moding"); return Task.CompletedTask; }));
        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "先行", "目标原逻辑", "先行" }, trace); // 仅「先行」（目标不再参与、幽灵注册无痕）
    }

    // ---------- 快照语义：进行中不打断；下一次执行立即生效 ----------

    [Fact]
    public async Task Moding_Changes_During_Execution_Do_Not_Break_InFlight_Selection()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "快照语义");
        var target = trigger.Register("目标", (v, c, t) => { trace.Add("原逻辑"); return Task.CompletedTask; });

        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();

        var m1 = trigger.RegisterReplaceWrapper(target, async (v, c, t) =>
        {
            trace.Add("m1 开始");
            entered.SetResult();
            await gate.Task; // 执行中挂起（等待测试注入增删操作）
            trace.Add("m1 结束");
        })!;

        var execution = trigger.InvokeAsync(engine);
        Assert.True(entered.Task.IsCompleted); // 执行已进入 m1 并挂起（已解析选择＝m1）

        // 进行中介入：注销 m1 ＋ 注册 m2（既解析选择不被打断）
        Assert.True(trigger.UnregisterWrapper(m1));
        var m2 = trigger.RegisterReplaceWrapper(target, (v, c, t) => { trace.Add("m2"); return Task.CompletedTask; })!;

        gate.SetResult(); // 放行 m1
        await execution;

        Assert.Equal(new[] { "m1 开始", "m1 结束" }, trace); // 本次执行完整跑完 m1；m2 不参与本次

        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "m1 开始", "m1 结束", "m2" }, trace); // 下一次执行：立即生效（m2 最后者胜）
    }

    [Fact]
    public async Task Moding_Registered_Mid_Iteration_Applies_At_Next_Handler_Resolution_Point()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "解析点");
        TriggerRegistration? target = null;

        trigger.Register("先行", (v, c, t) =>
        {
            trace.Add("先行");
            // 「目标」尚未执行（尚未解析）：此刻注册的 moding 自其下一次解析点（＝临执行前）生效
            trigger.RegisterReplaceWrapper(target!, (v2, c2, t2) => { trace.Add("moding"); return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        target = trigger.Register("目标", (v, c, t) => { trace.Add("原逻辑"); return Task.CompletedTask; });

        await trigger.InvokeAsync(engine);

        // 解析时点＝每次执行 handler 时（动态解析「当前最后一个」）：目标临执行时解析到新注册项
        Assert.Equal(new[] { "先行", "moding" }, trace);
    }

    // ---------- 构造期项可寻址（InitialRegistrations）；机制静默 ----------

    [Fact]
    public async Task Initial_Registrations_Provide_Addressable_Handles()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "构造期寻址", events: new[]
        {
            new TriggerEvent<CounterView>("初始", (v, c, t) => { trace.Add("初始原逻辑"); return Task.CompletedTask; }),
        });

        Assert.Single(trigger.InitialRegistrations);
        var handle = trigger.InitialRegistrations[0];
        Assert.Equal("初始", handle.Name);

        var m = trigger.RegisterReplaceWrapper(handle, (v, c, t) => { trace.Add("初始moding"); return Task.CompletedTask; });
        Assert.NotNull(m);

        var stream = await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "初始moding" }, trace); // 构造期装配项：经句柄注册的 moding 替换生效
        Assert.Empty(stream.Entries); // 机制静默：注册/注销/解析选择不留痕
    }

    // ---------- 用途代表场景②：流程内置处理器屏蔽替换 ----------

    [Fact]
    public async Task Usage_Shield_Replace_Flow_Built_In_Handler()
    {
        var engine = new LogicEngine();
        var attacker = new Card(engine, "甲");
        attacker.AddData(new HealthData { Hp = 10 });
        var defender = new Card(engine, "乙");
        defender.AddData(new HealthData { Hp = 10 });

        // 屏蔽「伤害生效」：经句柄把伤害结算流程的默认处理器替换为无操作（不新增前后 handler）
        var applyM = engine.DamageFlow.Trigger.RegisterReplaceWrapper(
            engine.DamageFlow.ApplyRegistration,
            (view, ctx, ct) => Task.CompletedTask);
        Assert.NotNull(applyM);

        await engine.AttackFlow.ExecuteAsync(attacker, defender, 5);
        Assert.Equal(10, defender.GetData<HealthData>().Hp); // 替换生效：伤害不生效

        // 注销 → 回退原逻辑：伤害照常
        Assert.True(engine.DamageFlow.Trigger.UnregisterWrapper(applyM!));
        await engine.AttackFlow.ExecuteAsync(attacker, defender, 5);
        Assert.Equal(5, defender.GetData<HealthData>().Hp);

        // 屏蔽「伤害结算」调用：经攻击流程内置句柄替换为无操作 → 攻击不再引发伤害结算
        var resolveM = engine.AttackFlow.Trigger.RegisterReplaceWrapper(
            engine.AttackFlow.ResolveRegistration,
            (view, ctx, ct) => Task.CompletedTask);
        Assert.NotNull(resolveM);

        await engine.AttackFlow.ExecuteAsync(attacker, defender, 5);
        Assert.Equal(5, defender.GetData<HealthData>().Hp); // 伤害保持（攻击结算不调用伤害结算流程）
    }

    // ---------- 替换作用域：仅该注册项；名称/排序位置不变 ----------

    [Fact]
    public async Task Replacement_Does_Not_Change_Name_Band_Or_Order()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "范围与排序");
        var first = trigger.Register("条目甲", (v, c, t) => { trace.Add("甲原"); return Task.CompletedTask; }, priority: 0);
        trigger.Register("条目乙", (v, c, t) => { trace.Add("乙"); return Task.CompletedTask; }, priority: 10);

        var m = trigger.RegisterReplaceWrapper(first, (v, c, t) => { trace.Add("甲改"); return Task.CompletedTask; });
        Assert.NotNull(m);
        Assert.Equal("条目甲", first.Name); // 句柄可读标识不变（替换仅作用处理回调本身）

        await trigger.InvokeAsync(engine);
        Assert.Equal(new[] { "甲改", "乙" }, trace); // 排序位置不变：甲（0）仍在乙（10）之前
    }

    // ---------- 异常隔离：沿用既有事件异常语义 ----------

    [Fact]
    public async Task Moding_Exception_Is_Isolated_Like_Handler_Exception()
    {
        var engine = new LogicEngine();
        var trace = new List<string>();
        var trigger = new Trigger<CounterView>(name: "异常隔离");
        var first = trigger.Register("一", Nop);
        trigger.Register("二", (v, c, t) => { trace.Add("二"); return Task.CompletedTask; });
        var m = trigger.RegisterReplaceWrapper(first, (v, c, t) => throw new InvalidOperationException("moding 故意爆炸"));
        Assert.NotNull(m);

        var stream = await trigger.InvokeAsync(engine);

        Assert.Equal(new[] { "二" }, trace); // moding 异常被隔离记录：后续事件继续执行
        Assert.Equal(ExecutionOutcome.Normal, stream.Outcome); // 记录与标记分离：结局仍 Normal
        Assert.Contains(stream.Entries, e => e.Keywords.Contains("exception:InvalidOperationException"));
    }
}
