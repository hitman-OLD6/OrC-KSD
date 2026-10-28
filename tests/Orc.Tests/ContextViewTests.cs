using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>验收点②权限（矩阵闸门 / 缺省回退 / 传播）与③绑定与改写（必填校验 / 读写 / 声明校验 / Seal）。</summary>
public class ContextViewTests
{
    private static Dictionary<string, object?> NewMatrixData(Entity? extra = null)
    {
        var target = new Entity("Target");
        var data = new Dictionary<string, object?>
        {
            ["Target"] = target.Ref,
            ["ReadOnlyAmount"] = 10,
            ["Amount"] = 10,
            ["Label"] = "label",
        };
        if (extra is not null)
        {
            data["Extra"] = extra.Ref;
        }

        return data;
    }

    private static Context NewMatrixContext(Entity? extra = null) => new(NewMatrixData(extra));

    [Fact]
    public void Read_Property_Readable_Write_Denied()
    {
        var view = ContextViewBinder.Create<MatrixView>(NewMatrixContext());

        Assert.Equal(10, view.ReadOnlyAmount); // [Read] 读允许

        // 访问当刻即抛：给 [Read] 属性赋值
        Assert.Throws<PermissionDeniedException>(() => { view.ReadOnlyAmount = 5; });
        Assert.Throws<PermissionDeniedException>(() => { view.Target = null!; });
    }

    [Fact]
    public void Undeclared_Property_Denied_For_Read_And_Write()
    {
        var view = ContextViewBinder.Create<MatrixView>(NewMatrixContext());

        Assert.Throws<PermissionDeniedException>(() => { _ = view.Undeclared; });
        Assert.Throws<PermissionDeniedException>(() => { view.Undeclared = 1; });
    }

    [Fact]
    public void Optional_Without_Main_Attribute_Denied_For_Read_And_Write()
    {
        var view = ContextViewBinder.Create<MatrixView>(NewMatrixContext());

        // 仅 [Optional] 而缺主特性 → 无有效访问特性：读写均拒绝
        Assert.Throws<PermissionDeniedException>(() => { _ = view.Draft; });
        Assert.Throws<PermissionDeniedException>(() => { view.Draft = 1; });
    }

    [Fact]
    public void Optional_Missing_Returns_Default()
    {
        var view = ContextViewBinder.Create<MatrixView>(NewMatrixContext());

        Assert.Null(view.Note);      // 引用类型 default
        Assert.Equal(0, view.Bonus); // 值类型 default
        Assert.Null(view.Extra);     // Ref 缺省 → default、不触发失效校验
    }

    [Fact]
    public void Optional_Ref_Alive_Readable_And_Dead_Throws()
    {
        var extra = new Entity("Extra");
        var view = ContextViewBinder.Create<MatrixView>(NewMatrixContext(extra));

        Assert.Same(extra.Ref, view.Extra); // 提供且存活 → 可读

        extra.Destroy();
        // [Optional] 且数据存在：读取时照常做失效校验
        Assert.Throws<StaleReferenceException>(() => { _ = view.Extra; });
    }

    [Fact]
    public void Mutate_Reads_Writes_And_Creates_Keys()
    {
        var ctx = NewMatrixContext();
        var view = ContextViewBinder.Create<MatrixView>(ctx);

        Assert.Equal(10, view.Amount); // [Mutate] 读允许
        view.Amount = 20;              // 改写既有值
        Assert.Equal(20, view.Amount);
        Assert.Equal(20, (int)ctx.Get("Amount")!); // 经载体可见（直连、不拷贝）

        view.Bonus = 7; // [Optional][Mutate] 缺省 → 写入创建新键（可增改）
        Assert.Equal(7, view.Bonus);
        Assert.Equal(7, (int)ctx.Get("Bonus")!);
    }

    [Fact]
    public void Required_Missing_Throws_KeyNotFoundException_At_Binding()
    {
        var missingKey = new Context(new Dictionary<string, object?>
        {
            ["Target"] = new Entity("T").Ref,
            ["ReadOnlyAmount"] = 1,
            ["Amount"] = 1,
            // "Label" 缺键
        });
        Assert.Throws<KeyNotFoundException>(() => { _ = ContextViewBinder.Create<MatrixView>(missingKey); });

        var nullValue = new Context(new Dictionary<string, object?>
        {
            ["Target"] = null, // 键存在但值为 null，同样视为缺失
            ["ReadOnlyAmount"] = 1,
            ["Amount"] = 1,
            ["Label"] = "x",
        });
        Assert.Throws<KeyNotFoundException>(() => { _ = ContextViewBinder.Create<MatrixView>(nullValue); });
    }

    [Fact]
    public void Read_Mutate_Conflict_Throws_At_Binding()
    {
        Assert.Throws<ArgumentException>(() => { _ = ContextViewBinder.Create<ConflictView>(new Context()); });
    }

    [Fact]
    public void Missing_ContextView_Attribute_Throws_At_Binding()
    {
        Assert.Throws<ArgumentException>(() => { _ = ContextViewBinder.Create<UntaggedView>(new Context()); });
    }

    [Fact]
    public void NonVirtual_Property_Rejected_At_Binding()
    {
        Assert.Throws<ArgumentException>(() => { _ = ContextViewBinder.Create<NonVirtualView>(new Context()); });
    }

    [Fact]
    public void Create_Null_Context_Rejected()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = ContextViewBinder.Create<CounterView>(null!); });
    }

    [Fact]
    public void Seal_Is_Idempotent_And_Does_Not_Relax_Matrix()
    {
        var target = new Entity("SealTarget");
        var ctx = new Context(new Dictionary<string, object?>
        {
            ["Target"] = target.Ref,
            ["ReadOnlyAmount"] = 1,
            ["Amount"] = 1,
            ["Label"] = "x",
        });
        var view = ContextViewBinder.Create<MatrixView>(ctx);
        var framed = (IContextView)view; // 框架产的实现框架侧契约

        framed.Seal();
        framed.Seal(); // 幂等：重复调用安全、无副作用

        // 封印不放松矩阵：读照常、写照拒、失效校验照常
        Assert.Equal(1, view.ReadOnlyAmount);
        Assert.Throws<PermissionDeniedException>(() => { view.ReadOnlyAmount = 5; });

        target.Destroy();
        Assert.Throws<StaleReferenceException>(() => { _ = view.Target; });
    }

    [Fact]
    public async Task Member_Execution_Does_Not_Isolate_PermissionDenied()
    {
        // §27.1 语义变更：成员执行面**不再把业务异常结局化**（旧 Trigger.InvokeAsync 的
        // "记录并继续、返回失败流"已删除）——越权写现在原样上抛（fail-fast）。
        var engine = new LogicEngine();
        var edge = Orc.Core.Model.MemberEdges.For<MatrixView>((view, ctx, ct) =>
        {
            view.ReadOnlyAmount = 5; // [Read] 写 → 当刻拒绝（PermissionDeniedException）
            return Task.CompletedTask;
        });
        var member = KernelTestKit.Member("权限联动", edge);

        var thrown = await Assert.ThrowsAsync<PermissionDeniedException>(
            () => KernelTestKit.RunAsync(engine, member, NewMatrixData()));

        Assert.Contains("只读", thrown.Message, StringComparison.Ordinal);
    }
}
