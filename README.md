# Faketorio

一个 Factorio 式工厂建造模拟游戏,Godot 4.5.1 Mono + C#/.NET 8。

## 项目简介

Faketorio 是一个从零实现的工厂自动化模拟:采矿 → 冶炼 → 组装 → 传送带物流 → 电力驱动,全程围绕一条**铁律**展开——模拟层必须完全确定(deterministic):同种子 + 同命令序列,任何时候在任何机器上跑,产生完全一致的状态。

## 架构

三层分离:

| 层 | 技术 | 职责 |
|---|---|---|
| **数据** | JSON + 纯 C# POCO(`Prototypes`) | 复刻 Factorio 的 prototype 体系,引擎无关,可脱离编辑器批量生成/校验 |
| **模拟** | `Faketorio.Sim`(纯 C#,零 Godot 依赖) | 固定 60 UPS,无头可测(xUnit),状态经 `IStateWriter` 规范序列化 + FNV-1a 哈希 |
| **表现** | Godot 节点 | 只读渲染模拟状态,玩家操作一律经命令提交,不直接改状态 |

选 POCO + JSON 而非 Godot `Resource` 的原因:① Godot C# 的 `Resource` 派生类离开引擎运行时无法实例化,会让 xUnit 无头测试不成立;② 数据可脱离编辑器批量生成/校验;③ Factorio 自身的数据也是引擎外置的。

### 确定性铁律

- 固定 tick(60 UPS),模拟层**禁止 `float`/`double` 参与任何影响状态的计算**——位置、能量、进度全用 `int`/`long`,比例用 Q16.16 定点数。
- 禁止依赖迭代顺序不定的容器(`Dictionary` 只能点查,序列化前必须显式排序)。
- 同种子 + 同命令序列 → 全状态哈希在任意 tick 都必须一致,这是每个子系统合并前的强制验收项。

详见 [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](docs/superpowers/specs/2026-07-03-faketorio-design.md)。

## 当前进度

M1(垂直切片)闭环:挖矿 → 冶炼 → 组装 → 存储,全程电力驱动。已合并:

- **P1** 模拟核心地基(原型系统 / 实体池 / 世界网格 / 命令队列 / 状态哈希)
- **P2/P3** 传送带(FFF-176 gap 表示法,合并/拆分,拐角交接)
- **P4** 库存(`ItemStack` + `Inventory` + 箱子接入)
- **P5** 玩家(移动 + 碰撞 + 手挖 + 手搓 + 开局物资包)
- **P6** 矿脉生成(定点值噪声,种子确定)
- **P7** 电网(电线杆连通分量 + 供需结算 + 燃料发电机)

进度快照与后续路线图见 [`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md)。

## 快速开始

需要 .NET 8 SDK。

```bash
# 跑全部模拟层测试
dotnet test sim/Faketorio.Sim.Tests

# 只跑某个测试类/方法
dotnet test sim/Faketorio.Sim.Tests --filter "FullyQualifiedName~SimulationTests"

# Release 构建(应为 0 警告 0 错误)
dotnet build -c Release
```

Godot 编辑器打开根目录即可(需要 Godot 4.5.1 Mono 版)。

## 项目结构

```
sim/
  Faketorio.Sim/            模拟核心(纯 C#,零 Godot 依赖)
    Prototypes/             数据原型(物品/实体/配方/资源/玩家/电网……)
    Entities/               通用代数 ID 实体池
    World/                  世界网格 + 矿脉生成(值噪声)
    Belts/                  传送带(FFF-176 transport line)
    Items/                  库存系统
    Player/                 玩家状态(移动/挖掘/合成)
    Electric/               电网(连通分量/结算/发电机)
    Commands/               命令队列(玩家操作的唯一入口)
    State/                  规范序列化 + FNV-1a 哈希
  Faketorio.Sim.Tests/      xUnit 无头测试
data/base/                  游戏数据(JSON prototype)
docs/superpowers/
  specs/                    架构设计文档(每个子系统的设计基线)
  plans/                    实施计划(任务拆分,含合并状态标注)
```

## 文档

- 主设计文档:[`docs/superpowers/specs/2026-07-03-faketorio-design.md`](docs/superpowers/specs/2026-07-03-faketorio-design.md)
- M1 剩余路线图 + 进度快照:[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md)
- 各子系统设计与实施计划:`docs/superpowers/specs/` 与 `docs/superpowers/plans/`,按日期命名,每份已合并的计划文档头部标注了对应的主线提交范围。

英文版说明见 [`README_en.md`](README_en.md)。
