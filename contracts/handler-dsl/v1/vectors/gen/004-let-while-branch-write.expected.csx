Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var budget = (view.Units).Count;
    var __bound__actions_1 = budget;
    for (var __i__actions_1 = 0; __i__actions_1 < __bound__actions_1 && ((budget > 0)); __i__actions_1++)
    {
        if (((view.Units).Count == 0))
        {
            view.Defense = (view.Defense - 1);
        }
        else
        {
            view.Defense = (view.Defense + (int)Orc.Game.Cards.KeywordEffectActionFace.ReadValue(orcSelf, "self", "attack"));
        }
    }
};
