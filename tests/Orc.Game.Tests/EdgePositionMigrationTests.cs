using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Model;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// U5-a 等价证据集（band 消费者下线）：
/// <list type="bullet">
/// <item>三个旧 band 挂接点（重甲 `Initiate`／免疫 `Rewrite`／死亡改写 `Rewrite`）改为**外来挂载边**，
///   位次与旧段一致（都在成员自有边之前）；</item>
/// <item>旧 band 触发器的排序**不变**（位次恒 <see cref="TriggerEdgePosition.Mounted"/>；排序仍按 band → priority → 注册序）；</item>
/// <item>位次排序＝外来边 → 自有边 → 外来尾边（新模型的 before／自有边／after）。</item>
/// </list>
/// </summary>
public class EdgePositionMigrationTests
{
    private enum LegacyBands
    {
        First = 1,
        Second = 2,
    }




}
