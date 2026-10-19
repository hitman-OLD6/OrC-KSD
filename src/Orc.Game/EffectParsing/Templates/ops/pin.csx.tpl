// [pin] 压制 selector(sel={{sel}} side={{side}} unitType={{filterUnitType}} keyword={{filterKeyword}})
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    var targets = await runtime.SelectAsync(self!, new Orc.Game.Effects.EffectSelector({{sel}}, {{side}}, {{selZone}}, {{filterUnitType}}, {{filterKeyword}}, null, {{selThreshold}}));
    foreach (var target in targets)
    {
        await runtime.PinAsync(target, ct, self);
    }
}
