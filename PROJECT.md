# 项目约定（正本）

本文件只放**会约束日常操作**的约定，步骤细节放 `docs/`，两处互链、不复制同一段文字。

## 1. 跑全量测试：一次只允许一个执行者

- **同一时刻只允许一个执行者跑全量测试**（人或 agent 都算）。明确放弃「多 agent 并行跑全量」：
  8 个测试宿主会同时抢内存与磁盘，两个执行者并行会让耗时数字互相污染，也会在产物槽位里互相覆盖。
- 开跑前确认**可用内存 ≥ 2.5 GB**。低于此值不降级、不排队，只记录下来并在结论里标注，
  原因见「并行包络是仓库里的固定常量，不做运行时自适应」。
- 操作步骤与计时口径见 [docs/测试执行/README.md](docs/测试执行/README.md)。

## 2. 构建产物只有一个槽位

- 全仓（`src/` `tests/` `samples/` `tools/`）的 bin 输出统一落在 **`artifacts/slot`**，
  由仓库根 `Directory.Build.props` 的 `ArtifactsPath` 定义，**零参数**即命中。
- 例：`artifacts/slot/bin/Orc.Manager/debug/Orc.Manager.dll`。
- 不允许用 `--artifacts-path` / `-p:ArtifactsPath` 把输出改到别处：`Directory.Build.targets` 里的
  `Guard-ArtifactSlot` 会直接判 `error ORC0001` 让构建失败。
- 中间产物（`obj/`）留在各工程目录内，不进槽位。理由：本机 `dotnet restore` 处于损坏状态
  （见第 4 条），迁移 `obj` 会强制重新还原，等于让构建不可用。
- `src/**/bin/`、`tests/**/bin/` 是**废弃残留**：不删、不再刷新、**不得作为启动来源**。

## 3. 启动管理器只从槽位起

- `start-manager.bat` 从 `artifacts/slot/bin/Orc.Manager/debug/Orc.Manager.dll` 启动；
  槽位 DLL 缺失＝报错退出，**绝不回落到** `src/Orc.Manager/bin/`。
- 槽位 DLL 早于 `src/` 下任何 `.cs`/`.csproj` 时，脚本先 `dotnet build OrcEngine.sln --no-restore` 再启动。
- `~/.workbuddy/mcp.json` 里的 `orc-manager` 条目也必须指向槽位路径；
  用 `start-manager.bat --config-mcp` 重写一次即可（改完需重启宿主）。

## 4. 本机已知环境缺陷（不是本仓库的问题）

- `dotnet restore` 在本机必失败：`NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`。
  根因是 `Environment.GetFolderPath(CommonApplicationData)` 返回空，`XPlatMachineWideSetting` 构造时
  `Path.Combine(null, ...)`。**全新空工程同样复现**，与本仓库配置无关。
- 因此本机的构建/测试命令一律带 `--no-restore`；`dotnet` 也不在 PATH 里，需用完整路径
  `C:\Program Files\dotnet\dotnet.exe`。
- 需要新增 NuGet 包引用时先停下：当前无法还原。

## 5. 测试并行度

- 每个测试工程一份 `tests/<工程>/xunit.runner.json`，`maxParallelThreads` 按宿主资源画像分档
  （长跑宿主大、起真实子进程的宿主小），是**仓库里的固定常量**，不做运行时自适应。
- 调档纪律：一次只动一个宿主，记录改前/改后秒数。

## 6. 未完成事项（2026-10-28 测试提速会话移交，勿当已解决）

全量测试墙钟已从约 39 分钟降到约 147 秒（热缓存），但下面两件事**没有做**，且它们是「测试并行化」能验收的前提：

1. **测试源文件被删未提交**：工作区有 47 个测试源文件处于未提交删除状态（`Orc.Game.Tests` 33 个 / `Orc.Tests` 15 个），用例覆盖面由 2367 降至 2109。需裁决是恢复还是接受删除。
2. **基线不稳定**：同一配置连跑三次失败数 165 / 157 / 169（105 条结果翻转）；另有约 152 条「词条效果绑定核验失败」只在并发时出现（单跑 0 条、8 路 163 条），根因是 `KeywordEffectAssembly.VerifyBindings` 按进程级活面核验。**在基线稳定之前不要打开 `DisableTestParallelization`**，否则失败无法归因。

会话记录（含实测数据、四档扫描结论、D-1～D-13 决策）见
`.grill/sessions/archive/2026-10-28-test-suite-runtime/`。

