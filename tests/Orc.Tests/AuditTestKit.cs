using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 编排证据的断言面（S3-14／S14）：旧总线订阅读面（<c>Bus.GetSubscribers</c>／<c>EnumerateHooks</c>）的等价替代。
/// <para>
/// 断言对象是**审查图的边集合**（<see cref="LogicEngine.BuildAuditGraph"/>），不再依赖触发器显示名：
/// 归属用 <see cref="AuditMemberEdge.SourceObject"/> 的**引用**比对，来源用四类闭合枚举标注。
/// </para>
/// <para>
/// 与旧的对应关系：<c>Bus.GetSubscribers(card.destroyed) 含 "{效果}/dispose"</c>
/// ≡ <see cref="AssertHasDisposalEdge"/>（<c>engine#card.destroyed</c> 上存在来源＝该效果的
/// <c>framework:disposal</c> 边）。
/// </para>
/// </summary>
internal static class AuditTestKit
{
    /// <summary>某成员上的边（声明形态地址或裸成员名均可）。</summary>
    internal static IReadOnlyList<AuditMemberEdge> EdgesInto(LogicEngine engine, string member)
        => engine.BuildAuditGraph().EdgesInto(member);

    /// <summary>断言某来源对象在兜底信号上有 <c>framework:disposal</c> 边（注销兜底就位）。</summary>
    internal static void AssertHasDisposalEdge(LogicEngine engine, object source, string? signal = null)
    {
        var edges = EdgesInto(engine, signal ?? Updates.CardDestroyed);
        Assert.Contains(
            edges,
            e => e.Source == EdgeSource.FrameworkDisposal && ReferenceEquals(e.SourceObject, source));
    }

    /// <summary>断言某来源对象在兜底信号上**没有** <c>framework:disposal</c> 边（已卸载／回滚）。</summary>
    internal static void AssertNoDisposalEdge(LogicEngine engine, object source, string? signal = null)
    {
        var edges = EdgesInto(engine, signal ?? Updates.CardDestroyed);
        Assert.DoesNotContain(
            edges,
            e => e.Source == EdgeSource.FrameworkDisposal && ReferenceEquals(e.SourceObject, source));
    }

    /// <summary>断言某来源对象在指定成员上没有任何边（"已卸载"的等价读面）。</summary>
    internal static void AssertNoEdgeFrom(LogicEngine engine, string member, object source)
        => Assert.DoesNotContain(EdgesInto(engine, member), e => ReferenceEquals(e.SourceObject, source));

    /// <summary>断言某来源对象在指定成员上有边（"已挂载"的等价读面）。</summary>
    internal static void AssertHasEdgeFrom(LogicEngine engine, string member, object source)
        => Assert.Contains(EdgesInto(engine, member), e => ReferenceEquals(e.SourceObject, source));

    /// <summary>某成员上的边数（旧 <c>GetSubscribers(...).Count</c> 的等价读面）。</summary>
    internal static int EdgeCount(LogicEngine engine, string member) => EdgesInto(engine, member).Count;
}
