// [losePoint] amount={{amount}}（失去 n 个指挥点）
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    await runtime.LosePointsAsync(self!, {{amount}}, ct, self);
}
