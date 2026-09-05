# M1 剩余子系统:拆分与顺序

日期: 2026-09-02
状态: 已与用户确认的拆分基线
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(第 5 节模拟层、第 7 节路线图 M1)

## 进度快照(2026-09-05,更新)

`main` HEAD `0a1f8d1`;`dotnet test sim/Faketorio.Sim.Tests` = **369 passing**;`dotnet build -c Release` = 0 警告 0 错误。SDD 执行时的逐任务账本在 `.superpowers/sdd/<plan>/progress.md`,收尾即删——**本表是唯一的跨 plan 进度看板**,plan 文档的 `- [ ]` 复选框不反映状态。

| 子项目 | 状态 | 主线提交(合并后) |
|---|---|---|
| **P1** 模拟核心地基(原型/实体池/世界网格/命令队列/状态哈希) | ✅ 已合并·已验证 | `95db82c`..`180aa3c` |
| **P2** 传送带 FFF-176 gap 表示法(BeltLane) | ✅ 已合并·已验证 | `72e64ed`..`6ac95e0` |
| **P3a** BeltLane 合并/拆分原语 | ✅ 已合并·已验证 | merge `9bd0198` |
| **P3b** BeltLine + 放置期三路合并 | ✅ 已合并·已验证 | merge `87025f2` |
| **P3c** BeltLine 拆分(RemoveBelt) | ✅ 已合并·已验证 | merge `54e7457` |
| **P3d** BeltNetwork ↔ Simulation 接线 + 每 tick 推进 + 拐角交接 | ✅ 已合并·已验证 | merge `be551b4` |
| **P4** 库存(ItemStack + Inventory + InventoryPool + Inventories 接线) | ✅ 已合并·已验证 | `55539e8`..`58f65f1` |
| **P6** 矿脉生成(DeterministicHash + ValueNoise + ResourceGrid + seed 接线) | ✅ 已合并·已验证 | `f45232b`..`ee36c52` |
| **P5** 玩家实体 + 位置 + MovePlayer + 玩家背包 + HandMine + HandCraft + 开局物资包 | ✅ 已合并·已验证 | `2ad2b1f`..`a5f569d` |
| **P7** 电网(电线杆连通分量 + supply_area + 每 tick 每网结算 + 缺电降速 + satisfaction + 燃料发电机,P8 并入本项) | ✅ 已合并·已验证 | `f058345`..`bc9c8ae` |
| **P9** 加工状态机(熔炉自动匹配 + 装配机 SetRecipe 共用两趟 tick,satisfaction 等比降速) | ✅ 已合并·已验证 | `a9ed5c4`..`0c93ac1` |
| **P10** typed belt items + 电力采矿机(footprint 单目标找矿 + satisfaction 降速 + 输出 belt/箱子;并入了 typed belt items 前置项) | ✅ 已合并·已验证 | `4732384`..`0a1f8d1` |
| **P11** 机械臂(belt lane ↔ inventory 抓/放) | ⬜ 未开始 | — |
| 横切 · **typed belt items** | ✅ 已合并(并入 P10 Task 1) | `039748f` |
| 横切 · RotateEntity 命令 | ⬜ 未立项(单独小 plan) | — |
| 横切 · 实体休眠 / 活跃列表(§5.3 性能地基) | ⬜ 未立项 · **P9 已落地机器状态,现在可以立项**(P9 最终审查建议:两趟全量扫描 + 每 tick 重查 Inventories/Prototypes 是天然的优化落点) | — |
| 横切 · 基准场景 + UPS/分配量报告进 CI(§5.5) | ⬜ 未立项 · **建议 P7/P9 前立最小回归基线**(当前仓库无 CI / 无基准项目) | — |

完成度分层看:M1 模拟基础设施 + 采矿→加工→存储→电力全链条 ≈ P1–P10 已落地(挖矿/自动采矿→手搓→熔炼→装配→存储→电力,传送带现在带物品类型);机械臂(自动物流)未开始;表现层(Godot 工程 + UI)未开始且不在本系列 sim 计划范围。P7 执行期确认:发电机结算的多生产者取整用 Hamilton(最大余数法)分配以保证守恒且不超容量;`TransferFromEntity` 的搬回逻辑改为搬移前按目标容量夹紧,避免 readOnly 库存下的物品消失。P9 执行期确认:机器输入/输出实际落地成普通 `Inventory`(按 `role` 区分,非 readOnly)而非 P7 预想的 readOnly 库存,故上述修复目前仍是防御性代码,未被任何现有路径触发——一旦 P11 机械臂能从机器库存搬东西,`Simulation.MachineTickPostSettle` 的完成前重校验分支(§P9 spec)会从"防御性、不可达"变成真正会走到的分支,当时要把它从"重置进度"改成"冻结进度"以保持与进度推进阶段一致的语义(P9 最终审查已记录为一条待办)。P10 执行期确认:采矿机输出方向复用放置期 `Command.Rotation`(不建 `RotateEntity` 命令);采矿机目标格被外部(玩家手挖)挖空会触发 `MiningDrillTickPostSettle` 里对空矿格的 `ResourcePrototype` 强转崩溃,已在最终审查修复轮加 `cell.IsEmpty` 守卫——**这类"锁定的世界格子被别的系统改掉"的 race 是 P11 机械臂、以及任何未来能改 `ResourceGrid`/机器库存的系统都要小心的模式**。另:实体被销毁时手里的待放置物品(采矿机/传送带/箱子/P9 机器完成品)一律丢弃,M1 没有掉落到世界的机制,这是全代码库一致的既有行为,将来若要"销毁掉落"需单独立项统一处理所有实体。

下一步:**P11 机械臂**(belt lane ↔ inventory 抓/放,依赖 P4 + P9 + P10 的 typed belt items);或先做几个横切小项(`RotateEntity`、实体休眠/活跃列表、CI 基准场景)。

## 0. 背景

M1 传送带链(Plan 3a/3b/3c/3d)已全部合并进 `main`。M1 路线图剩下的部分——玩家、库存、手挖手搓、开局物资包、电力采矿机、机械臂、熔炉、装配机、箱子行为、燃料发电机 + 电线杆 + 电网结算、矿脉生成——之间有依赖,本文把它拆成顺序执行、各自独立可测的子项目(下称 P4、P5…,接在 Plan 1/2/3 之后)。

每个子项目走和 Plan 3 一样的完整闭环:**brainstorm → 写 spec → 写实施计划 → subagent 执行(实现/审查/修复/复审)→ 合并进 `main` → 再规划下一个**。前一个落地前不锁定后一个的细节。

## 1. 依赖图

```
 P4 库存 (ItemStack + Inventory)  ── 零前置依赖,地基
   │  └─ 顺带:箱子实体挂 Inventory,放置建、拆除毁(内容物按 §10 分层处理:结构层计数+返回)
   │
   ├──> P5 玩家(实体+位置、MovePlayer 命令、玩家 Inventory、HandMine、HandCraft、开局物资包)
   │       ↑ 需要 "能挖的东西"
   ├──> P6 矿脉生成(WorldGrid 上的资源层,种子确定;HandMine / 采矿机的 "脚下有没有矿")
   │
   ├──────────────┐
   │              ↓
 P7 电网(电线杆连通分量、supply_area 覆盖用电实体、每 tick 每网供需结算一次、
   │      usage_priority 六档、缺电按比例降速、satisfaction Q16.16 广播)
   │  └─ 燃料发电机(Burner 入 + Electric primary-output 出)—— P7 内或紧随的 P8
   │              │
   ├──────────────┼───────────────────────┐
   ↓              ↓                        ↓
 P9 机器加工状态机 (§5.3)               P10 电力采矿机
   ├─ 熔炉                              (footprint 下有矿 + 有电 → 产出物进
   └─ 装配机                             相邻箱 / 落到相邻 belt)
   需要 P4 + P7 + RecipePrototype         需要 P4 + P6 + P7 + 传送带(已完成)
                     │
                     ↓
 P11 机械臂(belt lane ↔ inventory 抓/放、够不够得着、摆臂节奏、
      带上物品增量追踪定位 §5.2 末)
      需要 P4 + 传送带(已完成)+(可选)P9/P10 作为抓放对象

 横切(插在自然位置,不单列 tier):
   · 实体旋转命令 RotateEntity —— 便宜;跟 P5,或单独小 plan;belt 旋转 = BeltNetwork 先 RemoveBelt 再 AddBelt
   · 实体休眠 / 活跃列表 (§5.3 性能地基) —— 等 P9 有了 "空转 / 缺料 / 输出满" 的机器状态再做最自然,单独一个 plan
   · 基准测试场景 + UPS/分配量报告进 CI (§5.5) —— 等 P9/P10 有得测了再搭
```

## 2. 顺序理由

- **P4 是唯一零依赖的地基。** 箱子、机械臂、所有机器都要 `Inventory`;当前 sim 完全没有(`entities.json` 里 `inventorySize` 字段没人读)。P4 顺带让箱子真正存东西,并按传送带 §10 的分层约定收尾"拆箱子/机器时内容物去哪":结构层摘下 + 计数 + 返回,策略层(谁在拆)以后再定。
- **P5 + P6 解开 §7 的自举死锁**(发电机要煤 → 煤要采矿机 → 采矿机要电):手挖第一批启动煤、手搓补建材、开局物资包给首批建材。这层做完就有了最小人力闭环。
- **P7 电网**是所有机器"能源检查"那一步的前置,但它自己不依赖库存,**可与 P4/P5/P6 并行推进**。
- **P9(熔炉+装配机,共用加工状态机)与 P10(采矿机)可并行**,都依赖 P4+P7(P10 还要 P6)。
- **P11 机械臂最后**——它是连接器,要 belt(已完成)和 inventory 都在才有意义。
- **M1 收尾闭环**(挖矿→冶炼→组装→存储,全程电力驱动)在 P9+P10+P11 落地后跑通。

## 3. 各子项目范围速览

| 子项目 | 交付 | 前置 |
|---|---|---|
| **P4 库存** | `ItemStack`(item proto id + 数量,值类型)、`Inventory`(定长槽位数组,SoA 友好,insert/remove/query/WriteState)、箱子实体挂 `Inventory`(放置建/拆除毁)、拆除时内容物计数返回 | 无 |
| **P5 玩家 + 手动** | 玩家实体 + 位置、`MovePlayer` 命令、玩家 `Inventory`、`HandMine`(目标格 minable 实体 → 移除 + `minable.result` 进背包,按 `miningTimeSeconds` 进度)、`HandCraft`(配方 → 从背包扣输入 + 进度 + 产出)、sim init 时播开局物资包 | P4、P6 |
| **P6 矿脉生成** | WorldGrid 上的资源层(平行网格或资源实体类型)、从种子确定性生成、`ResourcePrototype`(如 coal / iron-ore)、查询"某格资源类型 + 剩余量" | 无 |
| **P7 电网** | `ElectricPolePrototype`、电线杆放置维护连通分量、`supply_area_distance` 覆盖区把用电实体挂到网、每 tick 每网汇总结算一次(§5.4 六档 usage_priority)、供不应求按比例降速、`satisfaction` Q16.16 广播、燃料发电机(Burner 入 + Electric primary-output 出) | 无(发电机的燃料槽用 P4,可 P7 内或 P8) |
| **P9 加工状态机** | 熔炉 + 装配机共用:配方匹配 → 能源检查 → 进度推进(int64 定点)→ 完成时原子放置输出(全有或全无预检)、输出堵塞进 pending、完成前重校验输入(§5.3) | P4、P7、`RecipePrototype`(类已存在) |
| **P10 电力采矿机** | 每 tick:footprint 下有目标矿 + 有电(满意度)→ 按挖掘速度累进 → 产出物进相邻箱 / 落到相邻 belt lane | P4、P6、P7、传送带 |
| **P11 机械臂** | 抓源(belt lane / inventory)、放目标(belt lane / inventory)、`reach` 判定、摆臂节奏、带上物品增量追踪定位(§5.2 末) | P4、传送带 |

横切三项(旋转命令、休眠活跃列表、基准场景)按依赖图注释里的时机插入,各自单独一个小 plan。

## 4. 明确不做(M1 内不碰)

- 分离器、地下传送带(M2)
- 科技树 / 实验室 / 科技包(M2)
- 存/读档(M2,基于铁律 4 的规范序列化——每个子项目照旧把新状态纳入 `WriteState`)
- 流体系统(M3)
- 表现层(Godot 节点、背包/机器面板 UI):不在 sim 计划范围;sim 只保证命令入口 + 可读状态

## 5. 下一步

从 **P4 库存** 开始:单独 brainstorm → spec → 实施计划 → 执行 → 合并。P4 spec 里要敲定的开放点(留给 P4 的 brainstorm):`ItemStack` 的确切字段与空槽表示、`Inventory` 是定长槽位还是"物品类型→总量"字典、槽位过滤 / 堆叠上限来自哪(`ItemPrototype.stackSize`?)、`WriteState` 的遍历序、拆箱子内容物返回的形状(计数 vs `List<ItemStack>`)。
