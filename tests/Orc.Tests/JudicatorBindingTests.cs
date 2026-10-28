using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 判定器绑定（J2）机制级测试：对局路径（注册表条目锚——moding 全局生效、注销回退）／
/// 独立实例锚（构造即可用）／解析动作即校验（未注册名 fail-fast）／工厂参数校验。
/// <para>
/// v4 迁移：绑定不再挂在触发器实例上（<c>Trigger.BindValidation</c> 随类型删除），
/// 而是把 <see cref="JudicatorBinding"/> 直接交给消费者（<c>MemberDefinition.Validation</c> 是另一条独立面：
/// 成员级委托校验）。因此本文件改为对绑定对象本身断言——绑定语义（注册表锚／独立锚／moding 生效）未变。
/// </para>
/// </summary>
public class JudicatorBindingTests
{
    private const string ProbeName = "test.binding.probe";

    private static readonly IReadOnlyList<Ref<Entity>> NoRefs = Array.Empty<Ref<Entity>>();

    [Fact]
    public void FromRegistration_Binds_To_Registry_Entry_Moding_Applies_And_Falls_Back()
    {
        var registry = new JudicatorRegistry();
        var handle = registry.Register(ProbeName, new ProbeJudicator(_ => ValidationVerdict.Valid));
        var binding = JudicatorBinding.FromRegistration(handle);

        Assert.True(binding.Validate(NoRefs, subject: null).IsValid); // 默认逻辑

        // moding 改写（统一面）→ 绑定调用生效（同一条目——全局生效）
        var m = registry.RegisterReplaceWrapper(handle, _ => new object[] { ValidationVerdict.Invalid() });
        Assert.NotNull(m);
        Assert.False(binding.Validate(NoRefs, subject: null).IsValid);

        // 注销回退（全部注销＝回退默认逻辑）
        Assert.True(registry.UnregisterWrapper(m!));
        Assert.True(binding.Validate(NoRefs, subject: null).IsValid);
    }

    [Fact]
    public void FromRegistration_Unregistered_Name_Fails_Fast_At_Resolve()
    {
        var registry = new JudicatorRegistry();

        // 绑定动作＝按名解析＋固定（解析动作即校验——未注册名＝装配期显性失败）。
        Assert.Throws<KeyNotFoundException>(
            () => JudicatorBinding.FromRegistration(registry.Resolve("test.binding.missing")));
    }

    [Fact]
    public void FromStandalone_Is_Self_Contained_Without_Registry()
    {
        // 独立构造路径：无注册表、构造即可用（内置默认承载语义——这里以测试判定器演示）。
        var binding = JudicatorBinding.FromStandalone(
            "test.binding.standalone", new ProbeJudicator(_ => ValidationVerdict.Invalid()));

        Assert.False(binding.Validate(NoRefs, subject: null).IsValid);
    }

    [Fact]
    public void Factories_Validate_Arguments()
    {
        Assert.Throws<ArgumentNullException>(() => JudicatorBinding.FromRegistration(null!));
        Assert.Throws<ArgumentNullException>(() => JudicatorBinding.FromStandalone("n", null!));
        Assert.Throws<ArgumentException>(() =>
            JudicatorBinding.FromStandalone("  ", new ProbeJudicator(_ => ValidationVerdict.Valid)));
    }

    /// <summary>验证判定器探针（测试夹具）：统一逻辑注入。</summary>
    private sealed class ProbeJudicator : ValidationJudicator
    {
        private readonly Func<IReadOnlyList<Ref<Entity>>, ValidationVerdict> _logic;

        public ProbeJudicator(Func<IReadOnlyList<Ref<Entity>>, ValidationVerdict> logic) => _logic = logic;

        protected override ValidationVerdict ValidateLogic(IReadOnlyList<Ref<Entity>> refs, Ref<Entity>? subject)
            => _logic(refs);
    }
}
