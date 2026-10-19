// [draw] count={{count}}
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    await runtime.DrawAsync(self!, {{count}}, ct, self);
}
