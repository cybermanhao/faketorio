# M1 剩余子系统:拆分与顺序

日期: 2026-09-02
状态: 已与用户确认的拆分基线
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(第 5 节模拟层、第 7 节路线图 M1)

## 进度快照(2026-09-06,更新)

`main` 已含 P12(CI 回归基线)+ P13(活跃 id 列表,PR #1 已合)+ P14(表现层 v1)+ 传送带 lane 选边入料 + P15(玩家 WASD + 相机跟随 + 手挖)。`dotnet test sim/Faketorio.Sim.Tests` = **446 passing**;`dotnet test Faketorio.sln` = 446 + 26 `Presentation.Core.Tests`;`dotnet build -c Release` = 0 警告 0 错误。SDD 执行时的逐任务账本在 `.superpowers/sdd/<plan>/progress.md`,收尾即删——**本表是唯一的跨 plan 进度看板**,plan 文档的 `- [ ]` 复选框不反映状态。

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
| **P11** 机械臂(belt lane ↔ inventory 抓/放,转速模型 + satisfaction 降速 + 两段摆臂,按子格位置抓/插传送带) | ✅ 已合并·已验证 | `947abd8`..`7a06760` |
| 横切 · **typed belt items** | ✅ 已合并(并入 P10 Task 1) | `039748f` |
| 横切 · RotateEntity 命令 | ⬜ 未立项(单独小 plan;P10/P11 已用"放置期 `Command.Rotation` 定死"绕过,只有想让玩家事后转向才需要) | — |
| 横切 · 实体休眠 / 活跃列表(§5.3 性能地基) | 🟡 **P13:换迭代源已合并**(4 容器 `ActiveIds` / `GeneratorIds`,8 个 tick 循环从全实体扫描改成遍历活跃 id 列表;`delta` 挪出机械臂空闲阶段 A)· **真休眠 / 唤醒待后续独立子项** | `3d91b48`..merge `c99d9fe` |
| 横切 · 基准场景 + UPS/分配量报告进 CI(§5.5) | ✅ 已合并·已验证(P12) | `c42ef1d`..`66bce92` |
| 横切 · "销毁掉落物品"统一处理(所有实体) | ⬜ 未立项 · 目前全代码库一致丢弃(传送带/箱子/P9 完成品/P10 pending/P11 手上物品);P11 最终审查:**无电机械臂仍会执行阶段 A 抓取**,物品能被死机械臂从传送带上拿走并卡在手里(确定、有界、来电即恢复,但与 Factorio 不符),和"销毁掉落"一起考虑 | — |
| 横切 · 机器 role-1 输入库存加物品过滤 | ⬜ 未立项 · P9 model gap:机器输入库存无过滤,机械臂会往熔炉输入里推任何物品;P11 是第一个能自动大规模触发它的系统 | — |
| 横切 · 传送带 lane 左右语义 + 按几何选边入料 | ✅ 已合并·已验证 · 本地 `--no-ff` merge `69d9ed0`。`BeltLine.LaneA` = 行进方向左侧、`LaneB` = 右侧(与 `WorldView.DrawLaneItems` 约定一致,渲染器零改动)。新增纯函数 `BeltNetwork.FeedsRightLane(sf, bd)`:源朝向与带行进**平行**(同/逆)→ 右侧 lane;**正交** → 远端 lane。采矿机产出(`Simulation.cs:~689`)/ 机械臂放件(`Simulation.cs:~857`)从 `LaneA\|\|LaneB` 回退改成按 `FeedsRightLane` 选边,**目标 lane 满则源卡住、不溢到另一条**。机械臂抓取侧不变(仍 A 优先 B)。两条 lane 本身不交互(各自 `Advance`、交接同名对接),本就正确。golden 随行为变化重生成(`baselineNsPerTick` 仍 0);`BenchScenarioTests.GOLDEN_TICK_800` 锚点同步更新。 | `69d9ed0` |

完成度分层看:**M1 模拟层垂直切片的核心闭环已全部落地** ≈ P1–P11(挖矿/自动采矿 → 手搓/熔炼/装配 → 传送带带类型物流 → 机械臂自动搬运 → 存储,全程电力驱动且缺电降速,确定性状态哈希基建齐全)。剩下的都是横切优化项(实体休眠/CI 基准/RotateEntity/销毁掉落/输入过滤)和表现层(Godot 工程 + UI,不在本系列 sim 计划范围)。P7 执行期确认:发电机结算的多生产者取整用 Hamilton(最大余数法)分配以保证守恒且不超容量;`TransferFromEntity` 的搬回逻辑改为搬移前按目标容量夹紧,避免 readOnly 库存下的物品消失。P9 执行期确认:机器输入/输出实际落地成普通 `Inventory`(按 `role` 区分,非 readOnly)而非 P7 预想的 readOnly 库存,故上述修复目前仍是防御性代码,未被任何现有路径触发——一旦 P11 机械臂能从机器库存搬东西,`Simulation.MachineTickPostSettle` 的完成前重校验分支(§P9 spec)会从"防御性、不可达"变成真正会走到的分支,当时要把它从"重置进度"改成"冻结进度"以保持与进度推进阶段一致的语义(P9 最终审查已记录为一条待办)。P10 执行期确认:采矿机输出方向复用放置期 `Command.Rotation`(不建 `RotateEntity` 命令);采矿机目标格被外部(玩家手挖)挖空会触发 `MiningDrillTickPostSettle` 里对空矿格的 `ResourcePrototype` 强转崩溃,已在最终审查修复轮加 `cell.IsEmpty` 守卫——**这类"锁定的世界格子被别的系统改掉"的 race 是 P11 机械臂、以及任何未来能改 `ResourceGrid`/机器库存的系统都要小心的模式**。另:实体被销毁时手里的待放置物品(采矿机/传送带/箱子/P9 机器完成品)一律丢弃,M1 没有掉落到世界的机制,这是全代码库一致的既有行为,将来若要"销毁掉落"需单独立项统一处理所有实体。

P12 执行期确认:①计划里默认基准规模(scale 200 / 20000 tick)实测 ~28 分钟,对每 PR 的 CI 不可用——裁定 R5 缩到 **scale 50 / 5000 tick / 3 iterations**(~2–3 分钟),`ScenarioSentinel` 的 10 个 `DefaultScale*` 固定常量随之在 scale 50 重新实测钉死;`--scale`/`--ticks` 仍留作手动重压测。②`data/base` 铁矿石全局覆盖率仅 ~11%,一个单元的 4 台采矿机落在同一个 64 格噪声晶格里高度相关,实测 fed drill 只有 ~13%(scale 50)——裁定 R3 接受:采矿链(fed + idle 两条路径)、传送带、熔炉、机械臂、大电网结算都仍被压到,golden 哈希照样能抓任何行为变化;真要拉高需单独立项做矿脉层增强。③`bench/golden.json` 的 `baselineNsPerTick` 以 `0`(未校准)出厂——性能红线的"退化告警"这一半是**待激活**的:需要人从首次绿色 CI 的 `bench-report` artifact 读出 `minNsPerTick` 填回并提交(`bench/README.md` 有流程);哈希红线从合并起即生效。④`--require-gates`(CI 已带):防止有人改了 `DefaultScale`/`DefaultTicks` 或 golden 的 `scale`/`ticks` 却没重生成 golden——那样门禁本会静默 SKIP、CI 假绿,现在改成 exit 3 报红。⑤`IStepProfiler` 的分阶段计时确认了 ~85% 的每 tick 成本在 `Inserters`/`MiningDrills`/`Machines`/`Electric` 四个 O(n) 全量扫描里(scale 50:机械臂 44% / 采矿机 29% / 加工 15% / 电网 12%,传送带 <0.5%)——**这就是实体休眠优化的量化落点和对照台架**。

P13 执行期确认:换迭代源(4 容器 `ActiveIds`)对 bench(scale 50 / 5000 tick)的即时 UPS 收益**很小**:`minNsPerTick` 5_031_616 → 4_972_334(-1.2%),四个目标阶段 Machines -2.9% / Electric -2.1% / MiningDrills -0.3% / Inserters +0.1%(噪声内)。原因:scale 50 下 ~3050 个实体几乎全部存活,旧 `for i in 0..Capacity` 循环体本就每 tick 跑满 ~3050 次,省掉的只是 `TryGetById` + 类型检查(每次 ~10–15ns,JIT 很便宜);每个阶段的成本大头是**真实的逐实体工作**(传送带格查询、`GetLineAt`/`IndexOf`、库存扫描、`GetSatisfaction`),不是扫描开销 —— P12 的分阶段计时量的是"阶段耗时"不是"扫描耗时"。P13 正确的定位(spec §1)是"换路 + 清理",是真休眠子项(整个跳过空闲实体)的**前置**,大收益在那里。零 golden 哈希变化(纯性能改造成立)。经 PR #1 合并进 `main`(merge `c99d9fe`)。

P14 执行期确认:表现层第一个子项落地 —— `presentation/Faketorio.Presentation.Core`(纯 C# 库:`TickAccumulator` 固定步长累加器 + `WorldTransform` 坐标变换,xUnit 可测,进 `Faketorio.sln` + CI `test` job;CI `test` 命令改为 `dotnet test Faketorio.sln`)+ `game/`(Godot 4.5.1 Mono 工程,`<ProjectReference>` `Faketorio.Sim` + `Presentation.Core`,**不进 sln、不进 CI**,靠 `dotnet build` 编译检查 + 人工 F5 验收)。sim 层唯一改动:`ResourceGrid.PeekResourceAt`(无副作用矿脉读,防渲染器地图模式平移改哈希;纯新增,golden 不变)。架构:单进程,autoload `SimHost` 持唯一 `Simulation` 并在 `_Process` 按 1/60 步长 `Step()`;`WorldView` 立即模式 `_Draw` 每帧从 sim 现读现画(占位色块,无 sprite);`BuildController` 左键放 wooden-chest / 右键拆。相机双模式骨架:`Follow`(跟随 `ICameraTarget`,v1 目标是静止玩家)/ `Free`(地图模式:拖拽平移 + 滚轮缩放)。v1 **snap 不插值**(`TickAccumulator.Alpha` 预留)。执行期裁定:(R3) `SimHost` 的启动小工厂从"直接往 lane 塞物品"(违反"命令外不碰 sim 状态")改成真实机械链(采矿机→带→机械臂→箱,煤启动矿斑供料),harness 验证 0 拒绝、物品可见流动;`CameraController.Transform` 因与 `Node2D.Transform` 冲突改名 `WorldXform`。§7.4 人工 F5 验收**已通过**,经 3 轮修复:(1)`36d03e6` 相机退出渲染路径——`Camera2D` 的 canvas 变换会和 `WorldTransform` 的偏移重复计一次(`TopLevel=true` 不豁免),删掉 `Camera2D`,`CameraController : Node` + `_centerTile` 字段,`WorldTransform` 成为唯一坐标映射;(2)`9fc4dd8` 矿脉 chunk 生成移出 `_Draw`——拉远/远距平移时 `_Draw` 里同步生成可见 chunk 造成卡顿,改成未命中入队、`FillPendingOreChunks` 每帧最多填 4 块;(3)`9df8c19` **裁定 R4**:上一步仍卡死,根因是逐格 `PeekResourceAt` 填一个 32×32 显示 chunk 要调 1024 次、每次在未生成 chunk 上重跑整块 `Generate` → O(chunk 格数²) ≈ 每帧 ~4M 次噪声。加**第二个 sim 读方法** `ResourceGrid.PeekChunk`(无副作用批量读,一个 chunk 一次 `Generate`,不进 `_chunks`、纯新增、golden 不变),渲染器改用它,每帧降到 ~4k;(4)`e2636f0` `_oreCache` 加 FIFO 有界淘汰(上限 512 chunk),防远距平移内存无限涨。表现层 v1 落地并合并进 `main`(本地 `--no-ff`)。**遗留(非阻塞 Minor):** `_oreCache` 淘汰是 FIFO 非 LRU——512 上限远大于可见集,实践无影响。

传送带 lane 选边入料(横切):新增纯函数 `BeltNetwork.FeedsRightLane` —— `LaneA` = 行进方向左、`LaneB` = 右;源朝向与带行进平行 → 右侧 lane,正交 → 远端 lane。采矿机产出 / 机械臂放件从 `LaneA||LaneB` 回退改成按此确定性选边,目标 lane 满则源卡住不溢出。渲染器零改动(`DrawLaneItems` 约定本就一致)。golden 随行为变化重生成(`baselineNsPerTick` 仍 0);`BenchScenarioTests.GOLDEN_TICK_800` 锚点同步更新;determinism 断言不受影响。本地 `--no-ff` merge `69d9ed0`。

P15 执行期确认(表现层后续第 1 项):纯表现层,**sim 零改动**(玩家八向行走 + 手挖 P5 已实现;golden 不变)。新增 `presentation/Faketorio.Presentation.Core/WalkInput.cs`(四方向键 → 八向 dir 纯函数,16 组合 xUnit)+ `game/PlayerInputController.cs`(`Node`,Follow 模式读 WASD → `MovePlayer`/`StopPlayer`,**仅方向变化 / 起停时发**,因 `MovePlayer` 是持久 sim 状态;按住 `E` → 对光标格 `MineStart`/`MineStop`)。`CameraController` 扩:Free 模式 WASD 平移相机(与中键拖拽并存)。`WorldView` 扩:玩家圆点按 `WalkDir` 画朝向刻度、手挖时光标格按 reach 着色。`game/project.godot` 加 InputMap `player_up/down/left/right/mine`(WASD+E)。模式仲裁靠 `CameraController.Mode` 单一裁决(Follow 归玩家、Free 归相机,无耦合);`Follow→Free` **不**停玩家(保留 walk 状态,留给后续锚定/带人移动模块)。整分支审查(opus)1 Important:Free 模式 reach 着色是虚假提示 → 改用 `sim.Player.Mining` 门控;+ 4 folded minor(`SubTilesPerTile` 常量、reach 比较与 sim `Isqrt` 边界对齐、缓存玩家原型、注释)。SDD 全 5 任务 clean,人工 F5 通过。裁定(preflight):`PlayerInputController` 内联 `WorldXform.ScreenToTile(mouse)`(同 `BuildController` 一行)而非抽 helper —— 读共享 transform,无逻辑漂移。本地 `--no-ff` merge。**遗留(F5 已确认的既有行为,非 bug):** 走进实体时整步拒绝(不滑墙),玩家仍 `Walking` 且画朝向刻度但零位移 —— 对新手像卡住,后续可加拒绝反馈。

下一步:核心 sim 闭环(P1–P11)+ CI 回归基线(P12)+ 活跃列表换路(P13)+ 表现层 v1(P14)+ 传送带 lane 选边入料 + P15(玩家 WASD/相机跟随/手挖)已完成。剩余候选(无强依赖顺序,按价值/成本挑):① **实体真休眠 / 唤醒**(§5.3 —— 给实体加"睡着"标记、tick 跳过、触发器唤醒;唤醒条件是确定性雷区,机械臂 vs 每 tick 在动的传送带最难,且和"睡着实体是否耗电"耦合;bench golden + 分阶段计时是现成对照台架;单独 brainstorm);② 机器输入库存加过滤 + "销毁掉落物品"统一处理;③ `RotateEntity` 命令(仅在要让玩家事后转向时);④ `bench/golden.json` 的 `baselineNsPerTick` 校准(P12 遗留的一次性人工步骤,从首次绿色 CI artifact 回填);⑤ **表现层后续**:视觉插值 lerp(要解决传送带物品跨 tick 身份匹配)、真美术(骨骼变换 vs 预渲染帧表)、完整命令 UI(快捷栏/背包/机器面板)、保留模式渲染(`TileMapLayer` + `MultiMesh`)——各自单独 brainstorm;⑥ **传送带带人移动(sim)**:传送带格可通行,玩家站上去按传送带方向被施加速度(顺向快、逆向慢),留一个"锚定模块"可覆盖此跟随的接口 —— 下一个 brainstorm。

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
