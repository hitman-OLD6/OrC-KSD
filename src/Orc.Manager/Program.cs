using Orc.Manager;

// 管理器进程入口：CLI 是进程内 API 的一次调用（同一真身，逐字等价）。
// stdout 只写结果 JSON；诊断一律 stderr。
return await Cli.RunAsync(args, Console.Out, Console.Error);
