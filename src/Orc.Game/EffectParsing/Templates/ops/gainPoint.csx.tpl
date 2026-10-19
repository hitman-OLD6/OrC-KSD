// [gainPoint] amount={{amount}}（获得 n 个指挥点）
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    await runtime.GainPointsAsync(self!, {{amount}}, ct, self);
}
