namespace Orc.Core;

/// <summary>
/// 判定器注册表（J1 机制骨架；对局级）：字符串名（ordinal）→ 判定器条目的注册表——
/// 「注册即配置」（装配期一次成型；重复注册拒绝）、未注册引用 fail-fast（按名解析/调用时明确失败、不设宽容旁路）；
/// moding（逻辑替换）注入面（双形态：统一签名底层＋强类型封装便捷面）与统一解析点（全局生效）；
/// 判定器条目自带等价句柄产物（注册所得＝解析所得——同一可寻址锚）。
/// 语义要素与 X4 handler moding 一致：栈语义（最后者胜、注销回退）、快照一致（一次调用＝一次解析）、
/// 幂等宽容（句柄面：跨注册表目标＝无操作不抛错、重复注销＝false）、句柄即权限（无注册者校验）、
/// 机制静默（moding 面无读面、注册/注销/解析不留痕）。
/// 无状态核验点：注册表仅持有装配期配置（条目）与机制状态（moding 栈）；判定器实例不持有对局状态。
/// 并发口径：条目注册单线程假设（装配期）；同步调用不承诺并发安全、快照＝同步语境一致性语义。
/// 实现＝<see cref="ServiceRegistry{TService,THandle,TModingHandle}"/> 的具体化（与 <see cref="AssetServiceRegistry"/>
/// **共用机制、名字域独立**——判定器名＝受控常量集合为唯一称谓来源，资产名非受控常量集）。
/// </summary>
public sealed class JudicatorRegistry : ServiceRegistry<Judicator, JudicatorRegistration, JudicatorWrapperRegistration>
{
    /// <summary>
    /// 注册一枚判定器条目（字符串名 ordinal；注册即配置——重复注册拒绝）。返回条目等价句柄
    /// （与按名解析所得为同一可寻址锚——注册方自持句柄、按名解析两通道等效）。
    /// </summary>
    /// <param name="name">判定器名（受控常量集合为唯一称谓来源；非 null、非空白）。</param>
    /// <param name="judicator">判定器实例（无状态服务）。</param>
    /// <returns>条目等价句柄（供持用：调用/moding 锚定）。</returns>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="ArgumentNullException">judicator 为 null。</exception>
    /// <exception cref="InvalidOperationException">同名条目重复注册（被拒绝——注册即配置）。</exception>
    public JudicatorRegistration Register(string name, Judicator judicator) => RegisterCore(name, judicator);

    /// <summary>
    /// 按名解析条目（字符串名 ordinal、逐字敏感）。返回条目等价句柄（与注册所得为同一可寻址锚——
    /// 「解析所得」与「注册所得」可等效用于 moding，杜绝解析口与 moding 口错位）。
    /// </summary>
    /// <param name="name">判定器名。</param>
    /// <returns>条目等价句柄。</returns>
    /// <exception cref="ArgumentException">name 为 null/空白。</exception>
    /// <exception cref="KeyNotFoundException">未注册引用（配置错误——fail-fast、不设宽容旁路）。</exception>
    public JudicatorRegistration Resolve(string name) => ResolveCore(name);

    /// <inheritdoc />
    protected override JudicatorRegistration CreateHandle(ServiceEntry<Judicator> entry) => new(entry);

    /// <inheritdoc />
    protected override JudicatorWrapperRegistration CreateWrapperHandle(ServiceEntry<Judicator> entry, long wrapperSeq)
        => new(entry, wrapperSeq);
}

/// <summary>
/// 判定器条目等价句柄（J1 可寻址产物）：注册动作返回；按名解析所得为同一实例（同一可寻址锚——
/// 两通道等效，moding 注册/注销经其锚定）。含 <see cref="ServiceRegistration{TService}.Invoke"/>（持有句柄的调用——
/// 每次调用经统一解析点取栈顶；引用获取与逻辑固定解耦：拿到句柄不等于锁定逻辑）。
/// 仅由注册面创建；不含可变状态。
/// </summary>
public sealed class JudicatorRegistration : ServiceRegistration<Judicator>
{
    internal JudicatorRegistration(ServiceEntry<Judicator> entry)
        : base(entry)
    {
    }
}

/// <summary>
/// moding（逻辑替换）注册句柄：<see cref="ServiceRegistry{TService,THandle,TModingHandle}.RegisterReplaceWrapper(THandle,Func{object[],object[]})"/>
/// 成功时返回，标识某条目上的一条 moding 项，可用作 <see cref="ServiceRegistry{TService,THandle,TModingHandle}.UnregisterWrapper"/>
/// 的注销依据（句柄即权限——不引入注册者校验/所有权面）。
/// 仅由注册面创建；不含可变状态。
/// </summary>
public sealed class JudicatorWrapperRegistration : ServiceWrapperRegistration<Judicator>
{
    internal JudicatorWrapperRegistration(ServiceEntry<Judicator> target, long wrapperSeq)
        : base(target, wrapperSeq)
    {
    }
}
