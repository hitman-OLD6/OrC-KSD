// [loseSlot] amount={{amount}}（失去 n 个指挥点槽）
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    await runtime.LosePointSlotsAsync(self!, {{amount}}, ct, self);
}
