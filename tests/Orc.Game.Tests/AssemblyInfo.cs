using Xunit;

// 卡包词条测试会向进程级 KeywordRegistry 注册词条/绑定（效果预制体只存在于该测试的引擎实例），
// 与并行执行的其他测试类存在窗口冲突（全局绑定核验）。禁用本程序集的测试并行以保证隔离。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
