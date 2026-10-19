// [shuffleIn] name={{name}} count={{count}}
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    await runtime.ShuffleInAsync(self!, {{name}}, {{count}}, ct, self);
}
