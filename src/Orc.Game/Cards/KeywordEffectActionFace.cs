using Orc.Cards;
using Orc.Game.Effects;

namespace Orc.Game.Cards;

/// <summary>
/// **csx 的死亡动作门面**（A2 加性）：词条行为资产（<c>Cards/KeywordPrefabs/*.component.json</c>）的 csx 成员
/// 需要「改写方自己执行死亡」时经本类调用——等价于旧效果时代经 <c>KeywordLoadContext.DeathAction</c> 的调用，
/// 目标单位经死亡动作走**完整两端**（触发端 <c>engine#death</c> → 处理端 <c>engine#death.handle</c>）。
/// <para>
/// **为什么单独一个类型而不是 <see cref="EffectRuntime"/> 的方法**：<c>EffectRuntime</c> 的公共成员集是
/// **契约冻结面**（<c>ContractFreezeTests</c> 逐条比对基线，变更需人工确认并重生成基线）。
/// 动作面（消灭／战斗／位移…）将来还会增加，逐个塞进冻结面会让每次加一条动作面都变成一次契约变更；
/// 本类不属于冻结面的任何一段，因此是动作面的**稳定落点**（新增方法不触碰契约门）。
/// </para>
/// <para>
/// 服务不可达／目标非单位＝<c>false</c>（零副作用、不抛错——沿「功能不可用＝不失败」口径）。
/// </para>
/// </summary>
public static class KeywordEffectActionFace
{
    /// <summary>
    /// 以死亡动作消灭目标（完整两端；<paramref name="host"/> 只用于解析对局服务）。
    /// </summary>
    /// <param name="host">服务解析宿主（行为组件所在卡；取自 csx 的 <c>view.Host</c>）。</param>
    /// <param name="target">被消灭的单位（其它实体类型＝false）。</param>
    /// <param name="killer">击杀者归属（<c>card.died</c> 的 <c>Killer</c>）。</param>
    /// <param name="triggerCard">触发者卡（链路传递；可空）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException">host／target 为 null。</exception>
    public static Task<bool> DeathAsync(
        Card host, Card target, Card? killer = null, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(target);

        return EffectRuntime.ResolveFor(host) is { } runtime
            ? runtime.DeathAsync(target, killer, triggerCard, ct)
            : Task.FromResult(false);
    }
}
