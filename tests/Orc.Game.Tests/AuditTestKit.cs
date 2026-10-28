using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 编排证据的断言面（S3-14／S14）：旧总线订阅读面（<c>Bus.GetSubscribers</c>／<c>EnumerateHooks</c>）的等价替代。
/// 断言对象是审查图的边集合，归属按 <see cref="AuditMemberEdge.SourceObject"/> 的引用比对（不依赖显示名）。
/// </summary>
internal static class AuditTestKit
{
    /// <summary>某成员上的边（声明形态地址或裸成员名均可）。</summary>
    internal static IReadOnlyList<AuditMemberEdge> EdgesInto(LogicEngine engine, string member)
        => engine.BuildAuditGraph().EdgesInto(member);

    /// <summary>断言某来源对象在兜底信号上有 <c>framework:disposal</c> 边（注销兜底就位）。</summary>
    internal static void AssertHasDisposalEdge(LogicEngine engine, object source, string? signal = null)
        => Assert.Contains(
            EdgesInto(engine, signal ?? Updates.CardDestroyed),
            e => e.Source == EdgeSource.FrameworkDisposal && ReferenceEquals(e.SourceObject, source));

    /// <summary>断言某来源对象在兜底信号上**没有** <c>framework:disposal</c> 边（已卸载／回滚）。</summary>
    internal static void AssertNoDisposalEdge(LogicEngine engine, object source, string? signal = null)
        => Assert.DoesNotContain(
            EdgesInto(engine, signal ?? Updates.CardDestroyed),
            e => e.Source == EdgeSource.FrameworkDisposal && ReferenceEquals(e.SourceObject, source));

    /// <summary>断言某来源对象在指定成员上没有任何边（"已卸载"的等价读面）。</summary>
    internal static void AssertNoEdgeFrom(LogicEngine engine, string member, object source)
        => Assert.DoesNotContain(EdgesInto(engine, member), e => ReferenceEquals(e.SourceObject, source));

    /// <summary>某成员上的边数（旧 <c>GetSubscribers(...).Count</c> 的等价读面）。</summary>
    internal static int EdgeCount(LogicEngine engine, string member) => EdgesInto(engine, member).Count;
}
