// [upgrade] 升为老兵（csx 对接 EffectRuntime.UpgradeAsync——S1 冻结的升级发动公共路径） selector(sel={{sel}} side={{side}})
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    var targets = await runtime.SelectAsync(self!, new Orc.Game.Effects.EffectSelector({{sel}}, {{side}}, {{selZone}}, {{filterUnitType}}, {{filterKeyword}}, null, {{selThreshold}}));
    foreach (var target in targets)
    {
        await runtime.UpgradeAsync(target, ct, self);
    }
}
