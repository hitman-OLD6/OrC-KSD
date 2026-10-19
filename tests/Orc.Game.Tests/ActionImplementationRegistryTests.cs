using Orc.Foundation.Actions;
using Orc.Game.Actions;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P5b / R17 验收：动作实现注册面——未注册 fail-fast、单动作覆盖优先、来源可查询。
/// <para>
/// 注册面是**进程级**单一真源，故本集合禁用并行，并在 finally 恢复夹具注册。
/// </para>
/// </summary>
[Collection(nameof(ActionImplementationRegistryCollection))]
public class ActionImplementationRegistryTests
{
    [Fact]
    public void Resolve_Throws_When_Nothing_Registered()
    {
        ActionImplementationRegistry.Reset();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => ActionImplementationRegistry.Resolve());
            Assert.Contains("未注册动作实现", error.Message);
        }
        finally
        {
            FoundationActions.Register();
        }
    }

    [Fact]
    public async Task Building_Match_Without_Registration_Throws_With_Clear_Message()
    {
        ActionImplementationRegistry.Reset();
        try
        {
            var match = GameTestData.CreateStandardMatch(seed: 42);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => match.Initialize());
            Assert.Contains("未注册动作实现", error.Message);
        }
        finally
        {
            FoundationActions.Register();
        }
    }

    [Fact]
    public void Register_Default_Makes_All_Eight_Actions_Come_From_Foundation()
    {
        ActionImplementationRegistry.Reset();
        try
        {
            FoundationActions.Register();

            var sources = ActionImplementationRegistry.Describe();
            Assert.Equal(8, sources.Count);
            Assert.All(sources.Values, source => Assert.Equal(ActionImplementationSource.Default, source));
        }
        finally
        {
            FoundationActions.Register();
        }
    }

    [Fact]
    public void Describe_Reports_Default_And_Override_Per_Action()
    {
        ActionImplementationRegistry.Reset();
        try
        {
            ActionImplementationRegistry.SetDefault(FoundationActions.Set);
            ActionImplementationRegistry.Override(ActionId.Move, MoveOnly());

            var sources = ActionImplementationRegistry.Describe();
            Assert.Equal(ActionImplementationSource.Override, sources[ActionId.Move]);
            Assert.Equal(ActionImplementationSource.Default, sources[ActionId.Damage]);
            Assert.DoesNotContain(ActionImplementationSource.Missing, sources.Values);
        }
        finally
        {
            FoundationActions.Register();
        }
    }

    [Fact]
    public void Resolve_Throws_With_Missing_ActionId_When_Default_Lacks_One()
    {
        ActionImplementationRegistry.Reset();
        try
        {
            // 只注册移动（其余 7 个缺失）：错误信息必须点名缺失的动作，而不是静默降级。
            ActionImplementationRegistry.SetDefault(MoveOnly());

            var error = Assert.Throws<InvalidOperationException>(() => ActionImplementationRegistry.Resolve());
            Assert.Contains("未注册动作实现", error.Message);
            Assert.Contains(ActionId.Damage.Value, error.Message);
        }
        finally
        {
            FoundationActions.Register();
        }
    }

    [Fact]
    public void Override_Rejects_Set_With_More_Than_One_Action()
    {
        ActionImplementationRegistry.Reset();
        try
        {
            ActionImplementationRegistry.SetDefault(FoundationActions.Set);
            var mixed = new ActionImplementationSet
            {
                CreateDamage = FoundationActions.Set.CreateDamage,
                CreateMove = FoundationActions.Set.CreateMove,
            };

            var error = Assert.Throws<InvalidOperationException>(
                () => ActionImplementationRegistry.Override(ActionId.Move, mixed));
            Assert.Contains("只能包含", error.Message);
        }
        finally
        {
            FoundationActions.Register();
        }
    }

    [Fact]
    public void Override_Keeps_Dependencies_On_Default_Implementation()
    {
        // R17 第 3 条：单动作覆盖后，该动作的依赖仍指向**未覆盖**的默认实现。
        ActionImplementationRegistry.Reset();
        try
        {
            ActionImplementationRegistry.SetDefault(FoundationActions.Set);
            var overriddenDraw = new ActionImplementationSet
            {
                // 刻意换一个委托实例，用来区分"覆盖"与"默认"。
                CreateDraw = (engine, services, addToHand) => FoundationActions.Set.CreateDraw!(engine, services, addToHand),
            };
            ActionImplementationRegistry.Override(ActionId.Draw, overriddenDraw);

            var resolved = ActionImplementationRegistry.Resolve();

            // 被覆盖的动作：来自覆盖集
            Assert.NotEqual(FoundationActions.Set.CreateDraw, resolved.CreateDraw);
            // 其依赖（加入手牌）：仍是默认实现，未被覆盖波及
            Assert.Equal(FoundationActions.Set.CreateAddToHand, resolved.CreateAddToHand);
            // 其余动作：仍是默认实现
            Assert.Equal(FoundationActions.Set.CreateMove, resolved.CreateMove);
            Assert.Equal(FoundationActions.Set.CreateDamage, resolved.CreateDamage);
        }
        finally
        {
            FoundationActions.Register();
        }
    }

    /// <summary>只含移动的单动作集合（覆盖注册面的最小合法形态）。</summary>
    private static ActionImplementationSet MoveOnly()
        => new() { CreateMove = FoundationActions.Set.CreateMove };
}

/// <summary>动作实现注册面测试集合（进程级状态；与其他集合并行会使 Reset 影响建局）。</summary>
[CollectionDefinition(nameof(ActionImplementationRegistryCollection), DisableParallelization = true)]
public class ActionImplementationRegistryCollection
{
}
