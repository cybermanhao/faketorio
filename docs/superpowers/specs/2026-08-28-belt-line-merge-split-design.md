# 传送带接入 Simulation:BeltLine 合并/拆分设计

日期: 2026-08-28
状态: 已与用户确认的设计基线
前置依赖: M1 Plan 1(模拟核心地基)、M1 Plan 2(`BeltLane` 核心算法,均已合并进 `main`)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(第 5.2 节"传送带(FFF-176 方案)"、第 9 节风险表)

## 1. 目标

把 Plan 2 已经做好、但仍是"独立数据结构、未接入 `Simulation`"的 `BeltLane`,变成玩家真正能放置、能运行、能拆除的传送带——并且实现 FFF-176 真正的性能收益:连续无打断的直线传送带合并成**一条共享的长线**,`Advance` 的开销与线的长度无关(O(1) 摊还),而不是每格之间做一次显式交接(那样开销是 O(链长),达不到 spec 5.2 的设计目标)。

## 2. 术语澄清(易混淆,先说清楚)

- **`BeltLane`**(Plan 2 已完成,`sim/Faketorio.Sim/Belts/BeltLane.cs`):一条 lane 上的物品流算法核心,只存物品间的相对 gap(整数亚格单位),提供 `TryInsertAtBack`/`Advance`/`IsFrontReady`/`RemoveFront`。不知道自己属于哪个传送带、朝哪个方向、在世界的什么位置。**本设计不改动它已有的这些方法**,只新增能力。
- **`BeltLine`**(本设计新增):一条**完整的逻辑传送带线**,组合(has-a,非继承)两个 `BeltLane` 实例(两条并行 lane),外加方向、覆盖哪些世界格子这些"贴到 `Simulation` 上"才需要的信息。一条 `BeltLine` 可能横跨多个物理传送带格子(合并后的结果)。

## 3. `BeltLane` 新增能力

在 Plan 2 已有的公开接口之上追加(不修改既有方法):

| 方法 | 用途 |
|---|---|
| `ExtendBack(int subtiles)` | 并入队尾方向的相邻线段:只增长 `_lineLengthSubTiles`,不碰任何 gap(`BackFreeSubTiles` 的公式自动反映新长度) |
| `ExtendFront(int subtiles)` | 并入出口方向的相邻线段:增长 `_lineLengthSubTiles`,给 `gaps[0]` 加上 `subtiles`(空线时无 gap 可加,跳过),**并把 `_openIndex` 重置为 0**——出口整体往外挪,原本被压缩到 0 的最前 gap 现在重新有了空间;游标若停在它后面(线曾处于堵塞态),最前物品会永远不向新出口前进 |
| `ToAbsolutePositions()` | 把 gap 列表转成"每个物品前沿距出口的绝对亚格距离"列表(前到后),一次线性扫描 |
| `static FromAbsolutePositions(int lineLength, IReadOnlyList<int> positions)` | 反向操作:从"前沿绝对距离"列表(前到后)和总长度重建一条新 `BeltLane`;`_openIndex` 直接取 0(唯一恒安全的初值)。相邻位置差 < `ItemWidthSubTiles` 说明物品重叠(上游 bug),fail-fast。**这同时是"把两条带物品的 `BeltLane` 拼接成一条"的构造块**,见第 5 节三路合并 |
| `TryRemoveItemInRange(int fromSubTile, int toSubTile)` | 摘除前沿落在 `[fromSubTile, toSubTile)` 内的物品(若有),摘除后把它前后两侧的 gap 缝合成一个(`RemoveFront` 是这个操作在"最前面、前面没有物品"这个特例下的简化版);无物品命中时返回 `false`,不改变状态。若被摘物品的下标 ≤ `_openIndex`,把 `_openIndex` 收回到该下标(下游物品重新有空间,与 `RemoveFront` 重置游标同理) |
| `WriteState(IStateWriter)` | 按既有模式(参照 `EntityPool<T>.WriteState`)写出 gap 数量 + gap 列表。**不写** `_openIndex`,也**不写** `TouchesInLastAdvance`:两者都是纯派生字段,加载后 `_openIndex` 置 0、下一次 `Advance` 多扫一趟即自愈,最终 gap 轨迹与状态哈希不受影响——状态哈希只认 gap 数值 |

`ItemWidthSubTiles`(64)、`Advance` 的摊还 O(1) 核心逻辑、`RemoveFront` 的既有语义全部不变。

## 4. `BeltLine`(新类型)

```
BeltLine {
    Direction: byte              // 复用 Command.Rotation 既有约定:0/1/2/3=北/东/南/西
    Tiles: List<(int x, int y)>  // 前到后排列,Tiles[0] 是出口所在格
    LaneA: BeltLane
    LaneB: BeltLane
}
```

两条 lane 同步同速前进,本设计不区分 A/B 的职能差异(机械臂具体要抓哪条 lane 是后续机械臂计划的事)。方向决定"背后邻格"(入口方向再往后一格)和"前方邻格"(出口方向再往前一格)——直接沿用 `Command.Rotation` 已有的方向约定,不新造概念。

### 存储与确定性

`BeltLine` 存进一个独立于通用 `Entities` 池的新池,复用 Plan 1 已验证的 `EntityPool<T>` 代数 ID 模式(按索引序确定遍历,供每 tick 更新与状态哈希使用)。每个属于某条线的传送带 `EntityId` 反查"属于哪条线、线内第几格",用一个和 `EntityId.Index` 对齐的稀疏数组(不用 `Dictionary`,避免非确定遍历风险)——这与 Plan 1 `WorldGrid` 用 tile 索引反查实体的思路一致。

## 5. 合并算法

放置一格新传送带时,检查它的"背后邻格"和"前方邻格":

- 邻格若是某条现有 `BeltLine` 的端点、且方向能对上(首尾能拼接,不是同向背对背或相对而立),则合并。**不要求两格是同一个 `TransportBeltPrototype`**(黄带红带同向相邻也会合并;混速线按出口格速度跑,见第 7 节):
  - 并到该线**入口端**:该线两条 lane 各调用一次 `ExtendBack(256)`,把新格追加进 `Tiles` 尾部。
  - 并到该线**出口端**:该线两条 lane 各调用一次 `ExtendFront(256)`,把新格插入 `Tiles` 头部。
- **两边都能接**(新格 T 恰好补在两段现有线 L1〔上游 / 入口侧〕、L2〔下游 / 出口侧〕之间的一格空隙,三路合并):对 A/B 两条 lane 各做一次——
  - `frontPos = L2.ToAbsolutePositions()`
  - `backPos  = L1.ToAbsolutePositions()`,每项加上 `256 + L2.len`(越过新格 T 和整条 L2)
  - `combined = frontPos ++ backPos`
  - `newLane  = BeltLane.FromAbsolutePositions(L1.len + 256 + L2.len, combined)`
  - `Tiles`:`L2.Tiles ++ [T] ++ L1.Tiles`;新 `BeltLine` 登记进池,L1/L2 旧槽位释放。

  这一步需要"两条各自带物品的 lane 拼接"能力——`ExtendBack(256)` 做不到,它只处理"加一个空格子"。拼接直接用 `ToAbsolutePositions`/`FromAbsolutePositions`(第 3 节,均属 Plan 3a),不需要专门的三路算法,但**依赖这对原语**。边界物品不会重叠:新格 T 在 L1、L2 之间留了一整格空隙。
- **两边都接不上**:新建一条只含这一格的 `BeltLine`(两条全新的、长度 256 的 `BeltLane`)。

合并只发生在放置时,不在每 tick 触发。

## 6. 拆分算法(移除传送带)

移除一格属于某条线的传送带:

- 若它是线的**端点**:直接从 `Tiles` 摘掉,两条 lane 缩短(出口端摘除时,先用 `TryRemoveItemInRange` 摘掉可能卡在该格范围内的物品——若摘不掉需先决定如何处理,见第 8 节风险;入口端摘除只需缩短长度)。
- 若它在线的**中间**:整条线拆成两条独立的线。两条 lane 各自:
  1. `ToAbsolutePositions()` 转成绝对位置列表
  2. 按被移除格子的亚格边界,把物品分成前后两组
  3. 各自用 `FromAbsolutePositions` 重建一条新 `BeltLane`(前半段沿用原有出口,长度=移除点之前的长度;后半段的出口是移除点后面那格的入口边界,长度=剩余长度)
  4. `Tiles` 列表相应一分为二,两条独立 `BeltLine` 各自登记进池

拆分不在每 tick 热路径上,只在玩家拆除时触发一次,不要求 O(1),只要求正确、可测。

## 7. 每 tick 系统更新

`Simulation.Step()` 在命令应用之后新增两步:

**1. 推进**:按 `BeltLine` 池的索引序(与 `WriteState` 遍历方式一致)遍历所有存活的线,对每条线的 `LaneA`/`LaneB` 各调用一次 `Advance(speed)`。

`speed` 取自**该线出口格**(`Tiles[0]`)的传送带 `TransportBeltPrototype.SpeedSubTilesPerTick`(Plan 2 已定义;沿用 Plan 2 的约束:该值应整除 `ItemWidthSubTiles`=64,否则 `Advance` 丢弃当 tick 剩余推进量会造成微小吞吐损耗)。合并不要求同 prototype(第 5 节),一条线可能跨越不同速度的格子——本设计的既定行为是**整条线按出口格速度跑**,不模拟"物品进入慢段减速"。精确分段速度留给分离器 / 多带种计划;M1 的基准与测试场景用单一带种,不触发这个近似。

**2. 线间交接(最简版)**:推进之后,再按池索引序遍历每条线,解析它出口格 `Tiles[0]` 的"前方邻格"——若该格属于另一条线 D 的入口端,则对 A/B 两条 lane 各做:
`while (本线.LaneX.IsFrontReady && D.LaneX.TryInsertAtBack()) 本线.LaneX.RemoveFront();`
前方邻格通过 `WorldGrid.GetEntityAt` + `EntityId → 线` 稀疏数组解析,不存下游引用(避免悬垂)。`TryInsertAtBack` 自带 64 亚格间距校验:下游空带时一次 tick 可搬多个物品(符合"空带自由铺入"),下游满带时自然停在 `IsFrontReady`。

遍历序固定(池索引),所以完全确定。顺序确实影响"某物品这一 tick 还是下一 tick 过界"(链式 A→B→C 中,先处理 A 可能让一个物品一 tick 内连过两个边界,仅在带子近空时发生),但序固定 ⇒ 回放必然复现,不需要拓扑排序。真正的线尾(没有下游线,也还没有机械臂 / 箱子接入)物品停在 `gaps[0]==0`,`IsFrontReady` 就是等待信号。

## 8. `IStateWriter` 覆盖

`Simulation.WriteState` 追加:按 `BeltLine` 池索引序,依次写每条线的 `Direction`、`Tiles`(数量+每格坐标,确定顺序)、`LaneA`/`LaneB` 的 `WriteState` 输出(第 3 节;gap 数量 + gap 列表,不含 `_openIndex`)。

## 9. 明确不做(留给更后续的计划)

- 机械臂的实际行为(够不够得着、抓取节奏、过滤器、增量追踪定位)——本设计只保证 `TryRemoveItemInRange` 这个原语存在,机械臂计划是纯粹的调用者。
- 玩家手动从传送带取物品的输入/UI/背包联动——同样只依赖 `TryRemoveItemInRange`,交互层不在本设计范围。
- 分离器(spec 5.2 提到的天然切分点)——分离器出现后,合并算法需要把"遇到分离器"也当作端点边界,这是后续计划的事,本设计的合并/拆分算法先只考虑"两个同向传送带直接相邻"这一种边界。
- 侧向汇入(sideloading:一条带垂直怼到另一条线的侧面喂入单侧 lane)——本设计中"贴在一条线中间而非端点"的新格一律落到"两边都接不上 → 新建单格线"。侧向汇入是后续计划。
- 混速合并线的精确减速——见第 7 节,本设计整条线按出口格速度跑,不模拟物品进入慢段减速。
- 传送带旋转导致的 footprint 宽高互换——Plan 2 已指出 `EntityData.Rotation` 存了但未使用,本设计的传送带固定 1×1,不涉及这个问题。

## 10. 风险与待确认点

- **移除的格子范围内恰好卡着一个物品该怎么办**:代码库目前完全没有"地面掉落物"这个概念(没有对应实体、没有对应 prototype),现在也不在这批计划的范围内引入它。因此拍板:`RemoveEntity` 处理传送带时,先对该格的亚格区间调用 `TryRemoveItemInRange`,命中就直接丢弃该物品(不生成任何替代实体),再执行端点摘除或中间拆分。这是本设计的既定行为,不是留给后续计划的开放项;若以后加入地面掉落物系统,再回来改这一步的处理方式。
- **合并/拆分与 `WorldGrid` 的 footprint 占位如何配合**:传送带都是 1×1,`Simulation.Apply` 现有的 `PlaceEntity`/`RemoveEntity` 逻辑(占位检查、拒绝计数)不用改,合并/拆分是在这套既有逻辑成功执行**之后**追加的一步后处理。

## 11. 实施拆分成多个计划

延续 M1 Plan 1→Plan 2 的节奏,拆成几个顺序执行、各自独立可测的小计划:

- **Plan 3a**:`BeltLane` 新增能力(`ExtendBack`/`ExtendFront`/`ToAbsolutePositions`/`FromAbsolutePositions`/`TryRemoveItemInRange`/`WriteState`)+ 独立单测,不碰 `BeltLine`/`Simulation`
- **Plan 3b**:`BeltLine` 数据结构 + 合并算法(含三路,拼接复用 3a 的 `ToAbsolutePositions`/`FromAbsolutePositions`)+ 独立单测,不碰 `Simulation`
- **Plan 3c**:拆分算法 + 独立单测,不碰 `Simulation`
- **Plan 3d**:接入 `Simulation`(放置/拆除触发合并拆分、每 tick 推进 + 线间交接最简版、`IStateWriter`)+ 贯通集成测试(含 L 形拐弯两条线的物品交接)+ 确定性测试(含存档/读档后 `_openIndex` 归零仍哈希一致)

每个计划都走完整的"实现→审查→(修复→复审)"闭环,前一个合并进 `main` 后再规划下一个的具体任务清单(避免过早锁定后面计划的细节,给中途发现的问题留调整空间)。
