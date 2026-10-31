Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var __runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(orcSelf);
    if (__runtime is null)
    {
        return;
    }
    var __bound__actions_0 = (view.Units).Count;
    for (var __i__actions_0 = 0; __i__actions_0 < __bound__actions_0; __i__actions_0++)
    {
        foreach (var u in view.Units)
        {
            await __runtime.DamageAsync(target: u, amount: 1, source: orcSelf, ct: ct);
        }
        ctx.Stop();
    }
};
