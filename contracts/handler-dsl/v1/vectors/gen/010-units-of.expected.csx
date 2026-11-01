Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var __runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(orcSelf);
    if (__runtime is null)
    {
        return;
    }
    var foes = await Orc.Game.Cards.KeywordEffectActionFace.UnitsOfAsync(host: orcSelf, side: "enemy", ct: ct);
    foreach (var f in foes)
    {
        await __runtime.DamageAsync(target: f, amount: 1, source: orcSelf, ct: ct);
    }
};
