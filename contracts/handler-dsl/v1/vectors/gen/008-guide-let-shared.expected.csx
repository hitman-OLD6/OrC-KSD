Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var __runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(orcSelf);
    if (__runtime is null)
    {
        return;
    }
    var n = (view.Units).Count;
    if ((n > 0))
    {
        await __runtime.DamageAsync(target: view.Unit, amount: n, source: orcSelf, ct: ct);
    }
};
