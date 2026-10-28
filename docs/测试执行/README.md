# 测试执行：跑全量、计时口径与并行度扫描

面向「要在本仓库跑一遍全量测试」的人。项目级约定（一次一个执行者、内存下限）在仓库根
[PROJECT.md](../../PROJECT.md)，这里只写怎么做与怎么量。

## 1. 一条命令跑全量

```bash
# 本机 dotnet 不在 PATH，用完整路径；restore 已损坏，必须 --no-restore
"C:/Program Files/dotnet/dotnet.exe" test OrcEngine.sln --no-restore
```

- 构建产物落在固定槽位 `artifacts/slot`（`Directory.Build.props` 定义），不需要任何额外参数，
  也不会再产生 `artifacts/test-<批次>` 目录。
- 想只跑一个程序集：

```bash
"C:/Program Files/dotnet/dotnet.exe" test tests/Orc.Game.Tests/Orc.Game.Tests.csproj --no-restore
```

- 冷启动（槽位不存在）：先 `build` 再 `test --no-build`，或直接 `test` 让它隐式构建。

## 2. 计时口径

- **计入**：命令发出 → 进程退出的全部时间，包括隐式构建、8 个宿主的 discovery、测试写盘。
- **不计入**：任何 `--logger trx` 的运行。trx 写盘会污染槽位、给每个宿主加收尾 IO，
  它的耗时**不可**与 300 秒目标对比。
- **热缓存样本**：改 `src/` 下一行等价语义的代码（不得只改注释、不得改公开签名），
  连跑两次、以第二次为准，两次都记录。
- **冷目录**：只冷槽位（重命名/删除 `artifacts/slot`），`~/.nuget/packages` 保持热。
  本机 `obj/` 不能删（删了无法 restore），因此「冷」到 bin 这一层为止。

## 3. 并行度扫描（调度无关性验证）

用途：证明「通过用例集合」不随并发度变化。它是**证伪工具，不是证明**——能抓出在
1/2/4/8 四档交错里暴露的泄漏，抓不到只在特定少量交错下出现的泄漏。结论只能写成
「未证伪 + 四档通过集合一致」。

```bash
# 前置：先构建过槽位
bash scripts/parallel-scan.sh 1 Game      # -> artifacts/scan-Game-p1/*.trx
bash scripts/parallel-scan.sh 2 Game
bash scripts/parallel-scan.sh 4 Game
bash scripts/parallel-scan.sh 8 Game
bash scripts/parallel-scan.sh 1 Packages
bash scripts/parallel-scan.sh 8 Packages
```

- 脚本只做参数化（用 runsettings 的 `<xUnit><MaxParallelThreads>` 覆盖 json 里的值）与产 trx，
  判定在脚本之外：解析 trx 里每条 `UnitTestResult` 的 `testName` + `outcome`，
  比对四档的**通过集合**是否逐一相同（不是比失败数）。
- 脚本带 trx，**其耗时不可与 R1 的 300 秒对比**。
- 不纳入日常流程：只在收缩 serial collection、删 `DisableTestParallelization` 兜底、最终验收时跑。

## 4. 已知坑

- 本机 `dotnet restore` 失败（机器级 SDK/环境问题，见 PROJECT.md 第 4 条），所以所有命令带 `--no-restore`。
- 常驻 Manager 进程锁着 `src/Orc.Manager/bin/Debug/net8.0/`；槽位重定向就是为了绕开它，
  不要杀那个进程。
- `artifacts/test-*` 是历史残留，不参与构建也不自动清理。
