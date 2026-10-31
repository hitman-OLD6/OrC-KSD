Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var __runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(orcSelf);
    if (__runtime is null)
    {
        return;
    }
    var targets = await Orc.Game.Cards.KeywordEffectActionFace.AskAsync(orcSelf, "unit", "enemy", null, "选一个敌方单位", "empty", ct);
    foreach (var t in targets)
    {
        await __runtime.DamageAsync(target: t, amount: 3, source: orcSelf, ct: ct);
    }
};
