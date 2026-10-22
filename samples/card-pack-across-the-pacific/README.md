# 跨越海洋 / Across the Pacific（card-pack-across-the-pacific）

独立卡包：7 张卡（3 张载具 + 4 张指令）+ 2 张美国步兵填充卡。
**卡与效果分离**：卡数据体（`cards/*.card.json`）只声明 `effects.prefabs` 引用，效果本体各自是独立资产（`effects/*.prefab.json`）。

## 目录

```
card-pack-across-the-pacific/
├── manifest.json        kind=cardpack / id=orc.across-the-pacific / name=跨越海洋 / version=内容指纹
├── lock.formal.json     正式 lock（本包 16 个资产的 GUID）
├── lock.test.json       测试 lock
├── cards/               9 张卡（每张带 .meta.json sidecar）
├── effects/             7 个效果预制体（每张带 .meta.json sidecar）
└── _tools/              构建与测试脚本（不参与装载）
```

## 卡表

| 卡 | 类型 | 费用 | 数值 | 效果预制体 |
|---|---|---|---|---|
| 滩头补给 | 指令 | 2 | — | `atp.beachhead-supply.effect` |
| 迅速登陆 | 指令 | 2 | — | `atp.rapid-landing.effect` |
| LVT-2 | 载具 | 2 / 行动 1 | 2 / 3 | `atp.lvt-2.aura` |
| LVT-4 | 载具（闪击） | 4 / 行动 2 | 4 / 5 | `atp.lvt-4.landing` |
| 两栖登陆 | 指令 | 3 | — | `atp.amphibious-landing.effect` |
| 大规模登陆 | 指令 | 5 | — | `atp.mass-landing.effect` |
| LVT-1 | 载具（闪击） | 3 / 行动 1 | 3 / 4 | `atp.lvt-1.landing` |
| 美国步枪兵 | 步兵 | 0 / 行动 1 | 1 / 2 | —（填充 + 大规模登陆卡池） |
| 第554步兵团 | 步兵 | 1 / 行动 1 | 1 / 2 | —（同上） |

## 效果实现要点

- **滩头补给**（`card.played`）：遍历对局内全部友方在场单位，统计步兵数 N；随机对友方单位执行 N 次 `+1/+1`（永久修饰）。
- **迅速登陆**（`card.played`）：经 `engine.Subscribe` 挂本回合监听；友方单位 `unit.deployed` / `unit.joined` 时随机对一个敌方目标（敌方单位或 HQ）造成 1 点伤害；`turn.end` 自注销。
- **LVT-2**（`card.load`）：登记两条持续光环（`attack +2` / `operateCost -1`），受益谓词＝`unit.adjacent` ∧ `unit.type=infantry` ∧ `unit.side`；随进出/位置变化实时重算。
- **LVT-4**（`unit.position.changed`）：仅当宿主单位移入**前线**槽（载荷 `OldPosition` 不在前线）时，给所有友方步兵 `+2 攻 / -1 行动花费`。
- **两栖登陆**（`card.played`）：前置＝前线有友方单位；把手中所有费用 ≤2 的单位逐张放置并推进到前线，再施加压制。
- **大规模登陆**（`card.played`）：从已注册定义里筛「美国 + 步兵 + 费用 ≤3」，随机取 4 张（同名可重复），逐张生成到支援阵线相邻空槽。
- **LVT-1**（`unit.position.changed`）：移入前线时，从手中费用 ≤2 的步兵里随机取 1 张，放置并推进到前线。

## 数据层约定

- 卡应用层 id＝`pack/orc.across-the-pacific/<局部 id>`；资产 GUID＝`SHA-256(包GUID + 种类 + 局部id + 8 段规范版本)` 前 16 字节（`Orc.Core.AssetIdentity.DeriveAssetGuid`）。
- **改卡必须同步 sidecar 与 lock**：版本变更会改变派生 GUID。改完跑一次 `_tools/build_pack.py` 重新生成最省事。
- 卡数据体缺 `image` / `description` 字段 → 装载为 `warning` 档（可玩，按类型默认值补缺）。

## 在沙盒里装载与测试

```bash
# 1) 起长存沙盒实例（sandbox + 写类调试工具）
dotnet src/Orc.Server/bin/Debug/net8.0/Orc.Server.dll \
  --packs samples/card-pack-across-the-pacific \
  --mode sandbox --allow-debug-writes --port 51899 \
  --sandbox outputs/atp-run1 --seats "[{\"kind\":\"ai\"},{\"kind\":\"agent\"}]" \
  --deck-rules lenient --seed 7

# 2) 另开 debug（--role debug）与座位（--role controller --seat 1）两条桥；
#    管理通道批准座位连接后 match.start；然后用 sandbox.* 摆局面、play.command 出指令。

# 3) 一键回归（脚本自带起服务、批准、摆局面与断言）
python _tools/test_effects.py all 51899
```

`_tools/test_effects.py` 的 7 个用例覆盖每张卡的核心行为（含「迅速登陆」的回合结束失效、LVT-2 的相邻/非相邻对照）。

## 分发打包

```bash
python scripts/pack_card_pack.py samples/card-pack-across-the-pacific --name 跨越海洋系列
# → dist/跨越海洋系列.zip（包目录一层 + 顶层「使用指南.md」；排除依赖仓库的开发脚本）
```

## 已知限制（数据层内可接受的近似）

1. **脚本沙箱文本屏蔽**：csx 源码不得出现 `Environment` 等片段，因此效果统一走 `engine.Cards` / `MatchCardService` 公开面，不直接访问战场环境；「是否在前线」按槽名（`前线[n]`）判定。
2. **无交互选牌面**：LVT-1 的「选择手中一张」以**随机取 1 张**落地（引擎当前没有手牌选靶 API）。
3. **加入前线采用两步**：手牌单位先落到支援线相邻空槽、再 `move frontline`（引擎没有「手牌直接入前线」的单一入口）；最终状态与卡面一致，中途会多发一次 `unit.position.changed`。
4. 两栖登陆/大规模登陆依赖支援线**相邻空槽**；阵线挤满时只能放入可放置的部分（卡面语义为「加入」，不保证数量）。
