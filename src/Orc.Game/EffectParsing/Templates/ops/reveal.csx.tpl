// [reveal] 揭示（管理动作域：候选来源＝RevealRules.CollectCovertUnits——CovertRules 读口包装、不经豁免剔除；执行＝EffectRuntime.RevealAsync 唯一标准发动口） count={{count}} selector(sel={{sel}} side={{side}})
var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
if (runtime is not null)
{
    var revealCandidates = Orc.Game.EffectParsing.RevealRules.CollectCovertUnits(self);
    var revealTake = {{count}};
    var revealCount = 0;
    foreach (var revealUnit in revealCandidates)
    {
        if (revealCount >= revealTake)
        {
            break;
        }

        await runtime.RevealAsync(revealUnit, ct, self);
        revealCount++;
    }
}
