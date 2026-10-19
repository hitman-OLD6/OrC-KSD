// [gainSlot] amount={{amount}}（额外获得 n 个指挥点槽）
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    await runtime.GainPointSlotsAsync(self!, {{amount}}, ct, self);
}
