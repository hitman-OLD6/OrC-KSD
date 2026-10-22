using Xunit;

// 本工程有 8 个测试类直接操作**进程级静态面**（`KeywordRegistry`、`BuiltInPlugin` 的一次性注册闩锁、
// `CardPackKeywordLoader.KeywordOwner`）。这些状态不随测试结束复位，并行跑会让"谁先注册词条"变成竞态——
// 表现为 `KeywordEffectAssembly.VerifyBindings` 偶发失败（词条 → 效果预制体绑定核验不过）。
// 关掉集合并行：代价是整体耗时略增，换来可复现的绿。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
