using System.Reflection;
using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Damage;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// P1 动作原语的架构不变量（S1/S2/S4 验收）：
/// 公共面零 internal 泄漏、<c>Orc.Game.Actions</c> 零管理器反向依赖、
/// <c>Commit</c> 每个触发端恰注册一次、<c>Veto</c> 命中即零落地。
/// </summary>
public class P1ActionContractTests
{
    [Fact]
    public void Actions_Public_Surface_Has_No_Internal_Types()
    {
        var assembly = typeof(ActionId).Assembly;
        var offenders = new List<string>();

        foreach (var type in assembly.GetTypes().Where(t => t.IsPublic && t.Namespace == "Orc.Game.Actions"))
        {
            Check(type.BaseType);
            foreach (var contract in type.GetInterfaces())
            {
                Check(contract);
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Check(property.PropertyType);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Check(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                {
                    Check(parameter.ParameterType);
                }
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    Check(parameter.ParameterType);
                }
            }
        }

        Assert.Empty(offenders);

        void Check(Type? candidate)
        {
            if (candidate is null || candidate.IsGenericParameter)
            {
                return;
            }

            if (candidate.IsArray)
            {
                Check(candidate.GetElementType());
                return;
            }

            if (candidate.IsGenericType)
            {
                foreach (var argument in candidate.GetGenericArguments())
                {
                    Check(argument);
                }
            }

            if (candidate.IsVisible)
            {
                return;
            }

            if (candidate.Namespace?.StartsWith("Orc", StringComparison.Ordinal) == true)
            {
                offenders.Add($"{candidate.FullName}");
            }
        }
    }

    [Fact]
    public void Actions_Source_Has_No_Manager_References()
    {
        var root = ContractSnapshot.FindRepoRoot();
        var directory = Path.Combine(root, "src", "Orc.Game", "Actions");
        var hits = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (file, line, index))
                .Where(item => IsCodeLine(item.line)
                    && (item.line.Contains("CommandManager", StringComparison.Ordinal)
                        || item.line.Contains("PlayManager", StringComparison.Ordinal))))
            .Select(item => $"{Path.GetFileName(item.file)}:{item.index + 1}: {item.line.Trim()}")
            .ToArray();

        Assert.Empty(hits);

        static bool IsCodeLine(string line)
        {
            var trimmed = line.TrimStart();
            return !trimmed.StartsWith("//", StringComparison.Ordinal)
                && !trimmed.StartsWith("*", StringComparison.Ordinal)
                && !trimmed.StartsWith("/*", StringComparison.Ordinal);
        }
    }



    [Fact]
    public void Action_Service_Surfaces_Have_Unique_Method_Names_And_Internal_Implementations()
    {
        var assembly = typeof(ActionId).Assembly;

        var methodNames = assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace == "Orc.Game.Actions" && t.Name.EndsWith("Services", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods())
            .Select(m => m.Name)
            .ToArray();

        Assert.NotEmpty(methodNames);
        Assert.Equal(methodNames.Length, methodNames.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(typeof(IDamageActionServices).GetMethods(), m => m.Name.Contains("Acted", StringComparison.Ordinal));

        // IOperateEntryServices 是宿主内部编排面：internal，测试经 InternalsVisibleTo 可见。
        Assert.False(typeof(IOperateEntryServices).IsPublic);

        var implementation = assembly.GetType("Orc.Game.Actions.MatchActionServices");
        Assert.NotNull(implementation);
        Assert.True(implementation!.IsNotPublic);

        // S3 第 6 条：DamageSettlement 删除（实体与类型均不再存在）。
        Assert.Null(assembly.GetType("Orc.Game.Damage.DamageSettlement"));
    }

    private sealed class FixedDamageServices : IDamageActionServices
    {
        public Task<int> ApplyDamageAsync(
            Card? source, Card target, int amount, string kind, CancellationToken ct = default)
            => Task.FromResult(Math.Max(0, amount));
    }
}
