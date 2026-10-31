using Orc.Core;
using Orc.HandlerDsl;

namespace Orc.Game.Effects;

/// <summary>
/// **handler DSL 的引擎侧装配**（S8／D4）：把「编译器 ＋ 目录」装成内核形态
/// <see cref="HandlerDslEnvironment"/>，由对局装配期赋给 <c>engine.HandlerDsl</c>。
/// <para>
/// 分层不变：内核（<c>Orc</c>）只持纯接口，实现（<c>Orc.HandlerDsl</c>）与扫描结果（本程序集的动作面）
/// 都在 satellite 侧；装载器拿到的是**装配件**，而不是「编译器 ＋ 一个它自己拿不到的目录」。
/// </para>
/// <para>
/// 目录在构造时**扫描一次**（<c>[HandlerDslActionFace]</c> 的门面集合，D10）——<c>catalogRevision</c> 由内容派生，
/// 同一份代码两次扫描必得同一个 revision（S4）。
/// </para>
/// </summary>
public static class HandlerDslAssembly
{
    /// <summary>
    /// 装配一份 handler DSL 环境（扫描本程序集的动作面）。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 目录生成失败（例如某动作面方法的返回对象缺 <c>[HandlerDslResult]</c> 拆解标注）——
    /// 那是**构建期就该发现**的错误，不在运行期静默降级成「没有 DSL 能力」。
    /// </exception>
    public static HandlerDslEnvironment Create()
    {
        var scan = HandlerDslCatalogScanner.Scan(typeof(EffectRuntime).Assembly);
        if (!scan.Success || scan.Catalog is null)
        {
            throw new InvalidOperationException(
                "handler DSL 目录扫描失败（动作面标注不完整）：" + string.Join("；", scan.Errors));
        }

        return new HandlerDslEnvironment(new HandlerDslCompiler(), scan.Catalog);
    }
}
