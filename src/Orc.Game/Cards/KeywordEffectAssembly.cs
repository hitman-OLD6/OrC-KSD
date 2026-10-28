using Orc.Cards;
using Orc.Core;

namespace Orc.Game.Cards;

/// <summary>
/// 词条效果装配结果（生产装配报告）：行为引用注册数 ＋ 库装载数 ＋ 库文件失败明细
/// （单文件失败＝隔离记录——报告承载；见 <see cref="KeywordEffectAssembly.Assemble"/>）。
/// </summary>
/// <param name="HandlersRegistered">行为引用注册数（键 → 委托）。</param>
/// <param name="PrefabsLoaded">词条效果库成功注册的预制体条数。</param>
/// <param name="LibraryFailures">库文件失败明细（文件名 + 原因；隔离——不阻断其余）。</param>
public sealed record KeywordEffectAssemblyResult(
    int HandlersRegistered, int PrefabsLoaded, IReadOnlyList<string> LibraryFailures);

/// <summary>
/// 词条效果生产装配（批 4 数据化）：单一装配入口——①行为引用注册（键 → C# 委托）＋②词条效果库装载
/// （预制体注册）＋③绑定核验（词条声明绑定 → 库键；配置错误 fail-fast）。
/// 挂于对局初始化链（<c>Match.Initialize</c>——管理器群装配段、卡牌加载之前）：正常对局初始化后，
/// 任何词条装载时行为引用与库键均已就绪/可解析（生产路径**不变量**）。
/// 每对局（＝每引擎实例）恰一次装配；「注册即配置、重复拒绝」——幂等以生命周期结构达成（初始化点恰一次）。
/// 失败语义：库文件层＝单文件失败隔离记录、不阻断其余（报告＋引擎总流留痕）；
/// 绑定层（键缺失）＝装配期 fail-fast（异常上抛——对局初始化失败）。
/// 行为引用键与效果预制体标识共用键名（本类为键一致性登记处；数据壳 JSON 内以字面量引用）。
/// </summary>
public static class KeywordEffectAssembly
{
    /// <summary>闪击·部署置位——效果预制体标识（＝数据壳 JSON 的 root.id 与 assemblyKey）。</summary>
    public const string BlitzDeploySetPrefabId = "keyword.blitz.deploy-set";

    /// <summary>动员·回合累积——效果预制体标识（＝数据壳 JSON 的 root.id 与 assemblyKey）。</summary>
    public const string MobilizeAccrualPrefabId = "keyword.mobilize.accrual";

    /// <summary>动员·受伤失去——效果预制体标识（＝数据壳 JSON 的 root.id 与 assemblyKey）。</summary>
    public const string MobilizeLossPrefabId = "keyword.mobilize.loss";

    // ---------- csx 行为引用样本（补全点 1；与生产试点并存——本单仅登记与装载，绑定维持 assemblyKey 版） ----------
    // 制品＝KeywordPrefabs/*.csx.prefab.json（csx 来源，无程序集键；解析经 LogicEngine.ScriptEvaluator）。
    // 后续迁移建议：绑定（KeywordRegistry.DeclareEffectBindings）切至下列标识、RegisterHandlers 对应项退役。

    /// <summary>闪击·部署置位（csx 行为引用样本）——效果预制体标识。</summary>
    public const string CsxBlitzDeploySetPrefabId = "keyword.blitz.deploy-set.csx";

    /// <summary>动员·回合累积（csx 行为引用样本）——效果预制体标识。</summary>
    public const string CsxMobilizeAccrualPrefabId = "keyword.mobilize.accrual.csx";

    /// <summary>动员·受伤失去（csx 行为引用样本）——效果预制体标识。</summary>
    public const string CsxMobilizeLossPrefabId = "keyword.mobilize.loss.csx";

    // ---------- A2（决策门 A）：7 个内建词条的 **v4 组件**标识（内建组件库 Cards/KeywordPrefabs/*.component.json） ----------
    // 行为承载＝组件成员的 csx（判定器经 EffectRuntime.Judge、死亡动作经 EffectRuntime.DeathAsync）；
    // 与上面 6 个「数据壳＋assemblyKey / 数据壳＋csx」旧形态标识**并存**（旧形态仅剩样本/测试用途，生产绑定已切下面这些）。

    /// <summary>闪击·部署置位（v4 组件）——组件资产标识。</summary>
    public const string BlitzDeploySetComponentId = "keyword.blitz.deploy-set";

    /// <summary>动员·回合累积（v4 组件）——组件资产标识。</summary>
    public const string MobilizeAccrualComponentId = "keyword.mobilize.accrual";

    /// <summary>动员·受伤失去（v4 组件）——组件资产标识。</summary>
    public const string MobilizeLossComponentId = "keyword.mobilize.loss";

    /// <summary>重甲·减伤（v4 组件）——组件资产标识。</summary>
    public const string ArmorComponentId = "keyword.armor";

    /// <summary>免疫·归零（v4 组件；单位侧伤害处理端）——组件资产标识。</summary>
    public const string ImmuneComponentId = "keyword.immune";

    /// <summary>伏击·伤害改写（v4 组件；攻击伤害处理端）——组件资产标识。</summary>
    public const string AmbushComponentId = "keyword.ambush";

    /// <summary>被压制·生命周期（v4 组件；回合结束递减/到期解除）——组件资产标识。</summary>
    public const string SuppressionComponentId = "keyword.suppression";

    /// <summary>情报·被使用时触发（v4 组件）——组件资产标识。</summary>
    public const string IntelligenceComponentId = "keyword.intelligence";

    /// <summary>重甲·减伤——效果名（卡容器检索面契约；v4 形态不实例化效果实例，名称仅作绑定可读标识）。</summary>
    public const string ArmorEffectName = "重甲·减伤";

    /// <summary>
    /// 内建词条组件 id 集（引擎自带、**任何对局都必须可解析**的 v4 组件）。
    /// 只用于装配期的绑定核验：包内词条不在此列，它们的可解析性由包装载门负责。
    /// </summary>
    private static readonly HashSet<string> BuiltInComponentIds = new(StringComparer.Ordinal)
    {
        BlitzDeploySetComponentId,
        MobilizeAccrualComponentId,
        MobilizeLossComponentId,
        ArmorComponentId,
        ImmuneComponentId,
        AmbushComponentId,
        SuppressionComponentId,
        IntelligenceComponentId,
    };

    /// <summary>免疫·归零——效果名（同上）。</summary>
    public const string ImmuneEffectName = "免疫·归零";

    /// <summary>伏击·伤害改写——效果名（同上）。</summary>
    public const string AmbushEffectName = "伏击·伤害改写";

    /// <summary>被压制·生命周期——效果名（同上）。</summary>
    public const string SuppressionEffectName = "被压制·生命周期";

    /// <summary>情报·被使用时触发——效果名（同上）。</summary>
    public const string IntelligenceEffectName = "情报·被使用时触发";

    /// <summary>闪击·部署置位——效果名（卡容器检索面契约；实例化时随数据壳装载）。</summary>
    public const string BlitzDeploySetEffectName = "闪击·部署置位";

    /// <summary>动员·回合累积——效果名（卡容器检索面契约；实例化时随数据壳装载）。</summary>
    public const string MobilizeAccrualEffectName = "动员·回合累积";

    /// <summary>动员·受伤失去——效果名（卡容器检索面契约；实例化时随数据壳装载）。</summary>
    public const string MobilizeLossEffectName = "动员·受伤失去";

    /// <summary>
    /// 生产装配（对局/引擎装配段调用）：
    /// ①行为引用注册（键 → 委托；注册即配置、重复拒绝）；②绑定核验（全部绑定引用的 v4 组件 id 须在编排目录里可解析；
    /// 缺失＝装配期 fail-fast）。
    /// <para>
    /// **库装载已删除**：旧词条效果库（<c>KeywordPrefabs/*.prefab.json</c>）的内存装载依赖
    /// <c>SnapshotStore.LoadDirectory</c>，随旧装载链一并删除；生产绑定已全部切到 v4 组件资产
    /// （<see cref="KeywordComponentLibrary"/> 负责把 <c>*.component.json</c> 装进编排目录）。
    /// </para>
    /// </summary>
    /// <param name="engine">对局引擎（预制体面＝<see cref="LogicEngine.Prefabs"/>）。</param>
    /// <exception cref="ArgumentNullException">engine 为 null。</exception>
    /// <exception cref="InvalidOperationException">绑定核验失败（键缺失——配置错误）。</exception>
    public static KeywordEffectAssemblyResult Assemble(LogicEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // ① 行为引用注册（程序集来源——显式；键→委托。与数据壳 JSON 的 assemblyKey 一致）。
        var handlers = RegisterHandlers(engine.Prefabs);

        // ② 绑定核验（词条声明绑定 → v4 组件 id；缺失＝配置错误——装配期 fail-fast，不得静默跳过）。
        VerifyBindings(engine);

        return new KeywordEffectAssemblyResult(handlers, 0, Array.Empty<string>());
    }

    /// <summary>行为引用注册（键 → 委托；注册即配置、重复拒绝——同一引擎实例重复装配被拒绝）。</summary>
    private static int RegisterHandlers(SnapshotStore prefabs)
    {
        prefabs.RegisterHandler(
            BlitzDeploySetPrefabId,
            (Func<CardEventView, Context, CancellationToken, Task>)BlitzDeployEffect.HandleDeploySetAsync);
        prefabs.RegisterHandler(
            MobilizeAccrualPrefabId,
            (Func<CardEventView, Context, CancellationToken, Task>)MobilizeAccrualEffect.HandleAccrualAsync);
        prefabs.RegisterHandler(
            MobilizeLossPrefabId,
            (Func<CardEventView, Context, CancellationToken, Task>)MobilizeLossEffect.HandleLossAsync);
        return 3;
    }

    /// <summary>
    /// 绑定核验（全部绑定：词条声明绑定 → **两种承载之一可解析**；缺失清单非空＝fail-fast）：
    /// v4 组件资产（编排运行时目录）或旧格式效果库键（<c>Prefabs</c>）——与装载期选路同一判据
    /// （见 <c>KeywordManager.TryAttachComponent</c>）。
    /// </summary>
    private static void VerifyBindings(LogicEngine engine)
    {
        var missing = new List<string>();
        foreach (var (keyword, binding) in KeywordRegistry.EnumerateEffectBindings())
        {
            // **只核验内建词条**：它们的组件由内建组件库（`Cards/KeywordPrefabs/*.component.json`）提供，
            // 任何对局都必须可解析——缺了是引擎自身的配置错。
            //
            // 包内词条（`keyword.mobility`／`keyword.entrench`／`keyword.magnetic-shield` …）的组件随
            // **包装载**进入编排目录，其缺失已由装载门（`CardPackAssembler` 的 failures → 整包跳过／必需的抛）
            // fail-fast。此处若用全局注册表核验，在"按需装包"下必然误报：本局没装 card-pack-bulwark，
            // 却要求 keyword.mobility 可解析——这正是 112 条对局用例集体失败的根因。
            if (!BuiltInComponentIds.Contains(binding.PrefabId))
            {
                continue;
            }

            if (engine.Model?.Catalog.Contains(binding.PrefabId) == true)
            {
                continue; // v4 组件资产（唯一承载形态）
            }

            missing.Add($"词条 '{keyword}' → 效果 '{binding.PrefabId}'");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "词条效果绑定核验失败（配置错误——装配期 fail-fast，v4 组件目录里找不到）："
                + string.Join("；", missing)
                + "。");
        }
    }
}
