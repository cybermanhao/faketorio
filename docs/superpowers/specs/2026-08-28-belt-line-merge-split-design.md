# 传送带接入 Simulation:BeltLine 合并/拆分设计

日期: 2026-08-28
状态: 已与用户确认的设计基线
前置依赖: M1 Plan 1(模拟核心地基)、M1 Plan 2(`BeltLane` 核心算法)、M1 Plan 3a(`BeltLane` 合并/拆分原语:`ExtendBack`/`ExtendFront`/`ToAbsolutePositions`/`FromAbsolutePositions`/`TryRemoveItemInRange`/`WriteState`,均已合并进 `main`)
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
| `ExtendFront(int subtiles)` | 并入出口方向的相邻线段:增长 `_lineLengthSubTiles`,给 `gaps[0]` 加上 `subtiles`(空线时无 gap 可加,跳过),**并把 `_openIndex` 重置为 0**。出口外移后 `gaps[0]` 重新非零;若不重置,游标可能停在它后面(线此前处于堵塞态时会如此),最前物品就永远不向新出口前进——重置消除这个隐患,Plan 3a 已实现并回归测试。第 5 节"单侧命中 `B`"依赖本方法此行为 |
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

**不变式**:因为合并只顺着同一方向、在端点处追加一格(第 5 节),每条 `BeltLine` 的 `Tiles` 恒为一段**共线的直线**;`lane.len` 恒等于 `Tiles.Count × 256`。

### 存储与确定性

- **`BeltLineId(int Index, int Generation)`**:`readonly record struct`,与 `EntityId` 同构——句柄 = 槽位 + 代数,用前校验代数一致以拦截"指向已删除并被复用的槽位"。
- **`BeltLinePool`**:装 `BeltLine`(引用类型)的池。分配器与 Plan 1 的 `EntityPool<T>` 同思路(高水位 `_count`、空闲下标栈、平行 `_generations` 偶死奇活、`Create`/`Destroy`/`IsAlive`/按索引序遍历/`WriteState`)。现有的 `EntityPool<T> where T : struct` **不改**(已合并、已测);分配器簿记若能干净抽成共享 helper 就抽,抽不干净则 `BeltLinePool` 带一份带注释的平行实现——确定性簿记只有一处真理。
- **`BeltNetwork`**:新类型,持有 `BeltLinePool` + 一个 **`tile → BeltLineId` 的分块稀疏索引**(行主序数组,分块方式与 `WorldGrid.Chunk` 一致,不用 `Dictionary`,避免非确定遍历)。公开 `AddBelt((x,y), dir, proto)`(触发合并)、按池索引序遍历、`WriteState`。合并的端点判定只查这个 tile 索引。
- 设计文档提到的另一个 **`EntityId → 线` 稀疏数组**,仅在 3d 接入 `Simulation`、处理"按传送带实体 ID 拆除"时才需要,推到 3d 再加。Plan 3b 的 `BeltNetwork` 只有 tile 索引。

## 5. 合并算法

`BeltNetwork.AddBelt((x,y), D, proto)`:先把新格 T 当作一条长度 256 的单格线登记(两条全新 `BeltLane(256)`),再检查 T 的"背后邻格"`B = (x,y) - delta(D)` 和"前方邻格"`F = (x,y) + delta(D)`:

- **精确端点判定**(只查 tile 索引):
  - `B` 上有线 `L` 且 `L.Direction == D` 且 `B == L.Tiles[0]`(`L` 的出口格)——`L` 喂进 T,把 T 并到 `L` 的**出口端**:`L` 两条 lane 各 `ExtendFront(256)`,`T` 插入 `L.Tiles` 头部。
  - `F` 上有线 `L` 且 `L.Direction == D` 且 `F == L.Tiles[^1]`(`L` 的入口格)——T 喂进 `L`,把 T 并到 `L` 的**入口端**:`L` 两条 lane 各 `ExtendBack(256)`,`T` 追加进 `L.Tiles` 尾部。
  - 邻格上有线但不是端点(T 贴在某条线的中段侧面)→ 不连,按侧向汇入处理(第 9 节),该侧当作接不上。
- 判定**不检查 `TransportBeltPrototype`**(黄带红带同向相邻也会合并;混速线按出口格速度跑,见第 7 节)。

开头登记 T 单格线时,已把 T 那格写进 tile 索引指向 T 线的 `BeltLineId`。下面每种情况在合并后都要**同步 tile 索引**——把所有并入的格子重指向存活/新建的线:

- **只命中一侧**:对该侧的线 `L` 各 lane 做一次 `ExtendFront(256)` 或 `ExtendBack(256)`(方向如上),T 进 `L.Tiles`;把 tile 索引里 **T 那格**改指向 `L.BeltLineId`;释放 T 单格线槽位。(`L` 原有格子的索引不变。)
- **两侧都命中**(T 恰好补在上游线 `L_up`〔`B` 侧〕与下游线 `L_down`〔`F` 侧〕之间的一格空隙,三路合并):沿流向顺序是 `L_up → T → L_down`。对 A/B 两条 lane 各做一次——
  - `frontPos = L_down.ToAbsolutePositions()`
  - `backPos  = L_up.ToAbsolutePositions()`,每项加上 `256 + L_down.len`(越过 T 和整条 `L_down`)
  - `combined = frontPos ++ backPos`
  - `newLane  = BeltLane.FromAbsolutePositions(L_up.len + 256 + L_down.len, combined)`
  - `Tiles = L_down.Tiles ++ [T] ++ L_up.Tiles`;新 `BeltLine` 登记进池得到 `newId`;把 tile 索引里 **`L_down`、T、`L_up` 的每一格**都改指向 `newId`;释放 `L_up`/`L_down`/T 单格线三个旧槽位。

  这一步需要"两条各自带物品的 lane 拼接"能力——`ExtendBack(256)` 做不到,它只处理"加一个空格子"。拼接直接用 `ToAbsolutePositions`/`FromAbsolutePositions`(第 3 节,均属 Plan 3a),不需要专门的三路算法,但**依赖这对原语**。边界物品不会重叠:新格 T 在 `L_up`、`L_down` 之间留了一整格空隙。
- **两侧都没命中**:什么都不做,一开始登记的 T 单格线(及其已写好的 tile 索引项)就是最终结果。

- **`L_up` 与 `L_down` 必为不同的线,无需防自环**:由第 4 节不变式,每条 `BeltLine` 是共线直线且 `Direction` 一致。若同一条 `L` 同时满足 `B == L.Tiles[0]`(在 T 之后)和 `F == L.Tiles[^1]`(在 T 之前),则 `L` 的出口在入口"后面",`L` 要逆着自己的 tile 序沿 `D` 流动——与不变式矛盾。且 `B → F` 唯一的共线通路必过 T,而 T ∉ `L`,所以 `B`、`F` 命中的端点分属两条不同的线。`AddBelt` 因此没有自环分支。一个几何闭环 = 若干直线 `BeltLine` 靠第 7 节的线间交接串成一圈循环。

合并只发生在放置时,不在每 tick 触发。

## 6. 拆分算法(移除传送带)

移除一格属于某条线的传送带:

- 若它是线的**端点**:直接从 `Tiles` 摘掉,两条 lane 缩短(出口端摘除时,先用 `TryRemoveItemInRange` 摘掉可能卡在该格范围内的物品,命中即丢弃,见第 10 节风险;入口端摘除只需缩短长度)。
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

**环形回路**就是这一步的一个闭合特例:矩形/任意闭环 = 若干直线 `BeltLine` 首尾相接成一圈(拐角格因方向不同各自成线),交接沿环一段段传。环没塞满时物品持续循环;环整圈塞满时,每条线出口都堵、前方线都收不下,整环冻住——`while` 条件自然给出这个结果,不需要额外的环检测。

## 8. `IStateWriter` 覆盖

`Simulation.WriteState` 追加:按 `BeltLine` 池索引序,依次写每条线的 `Direction`、`Tiles`(数量+每格坐标,确定顺序)、`LaneA`/`LaneB` 的 `WriteState` 输出(第 3 节;gap 数量 + gap 列表,不含 `_openIndex`)。

## 9. 明确不做(留给更后续的计划)

- 机械臂的实际行为(够不够得着、抓取节奏、过滤器、增量追踪定位)——本设计只保证 `TryRemoveItemInRange` 这个原语存在,机械臂计划是纯粹的调用者。
- 玩家手动从传送带取物品的输入/UI/背包联动——同样只依赖 `TryRemoveItemInRange`,交互层不在本设计范围。
- 分离器(spec 5.2 提到的天然切分点)——分离器出现后,合并算法需要把"遇到分离器"也当作端点边界,这是后续计划的事,本设计的合并/拆分算法先只考虑"两个同向传送带直接相邻"这一种边界。
- 侧向汇入(sideloading:一条带垂直怼到另一条线的侧面喂入单侧 lane)——本设计中"贴在一条线中间而非端点"的新格一律落到"两边都接不上 → 新建单格线"。侧向汇入是后续计划。
- 混速合并线的精确减速——见第 7 节,本设计整条线按出口格速度跑,不模拟物品进入慢段减速。
- 单条 `BeltLine` 的环形数据结构(带 wraparound 的 lane)——算法层不需要,环形回路的游戏体验由第 7 节的直线段 + 线间交接提供。
- 传送带旋转导致的 footprint 宽高互换——Plan 2 已指出 `EntityData.Rotation` 存了但未使用,本设计的传送带固定 1×1,不涉及这个问题。

## 10. 风险与待确认点

- **移除的格子范围内恰好卡着一个物品该怎么办**:代码库目前完全没有"地面掉落物"这个概念(没有对应实体、没有对应 prototype),现在也不在这批计划的范围内引入它。因此拍板:`RemoveEntity` 处理传送带时,先对该格的亚格区间调用 `TryRemoveItemInRange`,命中就直接丢弃该物品(不生成任何替代实体),再执行端点摘除或中间拆分。这是本设计的既定行为,不是留给后续计划的开放项;若以后加入地面掉落物系统,再回来改这一步的处理方式。
- **合并/拆分与 `WorldGrid` 的 footprint 占位如何配合**:传送带都是 1×1,`Simulation.Apply` 现有的 `PlaceEntity`/`RemoveEntity` 逻辑(占位检查、拒绝计数)不用改,合并/拆分是在这套既有逻辑成功执行**之后**追加的一步后处理。

## 11. 实施拆分成多个计划

延续 M1 Plan 1→Plan 2 的节奏,拆成几个顺序执行、各自独立可测的小计划:

- **Plan 3a**(已合并):`BeltLane` 新增能力(`ExtendBack`/`ExtendFront`/`ToAbsolutePositions`/`FromAbsolutePositions`/`TryRemoveItemInRange`/`WriteState`)+ 独立单测。
- **Plan 3b**:`BeltLineId` + `BeltLinePool`(引用类型池,分配器同 `EntityPool<T>` 思路,不改既有 struct 池)+ `BeltLine` 数据结构 + `BeltNetwork`(持有池 + `tile → BeltLineId` 分块索引)+ `AddBelt` 合并算法(单侧 / 三路 / 都不接;三路拼接复用 3a 的 `ToAbsolutePositions`/`FromAbsolutePositions`)+ 独立单测。任务清单里"合并后 **tile 索引重指向所有并入格子 + 释放旧线槽位**"要作为显式条目(尤其三路那条新线的整段重写),并有覆盖它的测试。不碰 `Simulation`、不碰 `Entities` 池、不做 `EntityId → 线` 反查、不做拆分。
- **Plan 3c**:拆分算法(`BeltNetwork.RemoveBelt`,端点摘除 / 中间拆分)+ 独立单测,不碰 `Simulation`
- **Plan 3d**:接入 `Simulation`(放置/拆除触发合并拆分、`EntityId → 线` 反查数组、每 tick 推进 + 线间交接最简版、`IStateWriter`)+ 贯通集成测试(L 形拐弯两条线的物品交接;矩形环的循环与塞满冻结)+ 确定性测试(含存档/读档后 `_openIndex` 归零仍哈希一致)

每个计划都走完整的"实现→审查→(修复→复审)"闭环,前一个合并进 `main` 后再规划下一个的具体任务清单(避免过早锁定后面计划的细节,给中途发现的问题留调整空间)。
