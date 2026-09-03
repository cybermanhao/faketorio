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
| `ShrinkBack(int subtiles)`(Plan 3c) | `ExtendBack` 的逆:入口端截短,`_lineLengthSubTiles -= subtiles`,不碰任何 gap。前置条件:被截区间 `[新长, 旧长)` 内没有物品——`BackFreeSubTiles() < subtiles` 时说明还有物品会被截飞,抛 `InvalidOperationException`(fail-fast,调用者须先清)。`_openIndex` 不变(截短只让 gap 更压缩,不违反"游标 ≤ 首个非零 gap 下标") |
| `ShrinkFront(int subtiles)`(Plan 3c) | `ExtendFront` 的逆:出口端截短,`_lineLengthSubTiles -= subtiles`,`gaps[0] -= subtiles`(空线跳过)。前置条件:`gaps[0] >= subtiles`(出口 `subtiles` 内无物品),否则抛 `InvalidOperationException`。`_openIndex` 不变(同上;`gaps[0]` 只会更小/归 0,不会重新非零) |

上表前六行属 Plan 3a(已合并);`ShrinkBack`/`ShrinkFront` 属 Plan 3c。`ItemWidthSubTiles`(64)、`Advance` 的摊还 O(1) 核心逻辑、`RemoveFront` 的既有语义全部不变。

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
- 本节早先设想的 **`EntityId → 线` 稀疏数组**,3d 评审时**取消**:3d 的所有接入都是按坐标的(`Simulation.Apply` 已用命令的 `(X,Y)` 查实体;线间交接解析前方邻格坐标),`tile → BeltLineId` 索引已足够。等机械臂计划里"手里握着带子 `EntityId` 想反查线"的需求出现时再加。`BeltNetwork` 只有 tile 索引。

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

`BeltNetwork.RemoveBelt(int x, int y)`:`_tiles.Get(x,y)` 找到线,线性扫 `line.Tiles` 拿到该格下标 `k` 与 `n = Tiles.Count`。被清下来的物品先摘下并**计数**——去向(丢弃 / 掉地上 / 进玩家背包)不是本设计决定的,见第 10 节。设 `L = BeltLine.TileSubTiles`(256)、`W = ItemWidthSubTiles`(64)。

- **`n == 1`**:`_pool.Destroy` 整条线,`_tiles.Clear(x,y)`,返回两条 lane 的物品数之和(`LaneA.Count + LaneB.Count`——它们随线一起销毁,全部计入丢弃)。
- **`k == 0`(出口端)**:两条 lane 各 `while (lane.TryRemoveItemInRange(0, L)) count++;` 清掉该格上的物品,再各 `ShrinkFront(L)`,`Tiles.RemoveAt(0)`。**线 id 不变**,`_tiles.Clear(x,y)`。
- **`k == n-1`(入口端)**:两条 lane 各 `while (lane.TryRemoveItemInRange((n-1)*L - (W-1), n*L)) count++;` ——低端向前拓宽 `W-1`,把"前沿在上一格、身体跨进被移格"的物品也清掉(否则 `ShrinkBack` 会把它截飞)。再各 `ShrinkBack(L)`,`Tiles.RemoveAt(n-1)`。id 不变,`_tiles.Clear(x,y)`。
  ——这修正了本节旧文"入口端摘除只需缩短长度":不先清物品会把它们截飞。
- **中间(`0 < k < n-1`)**:
  1. 两条 lane 各 `abs = ToAbsolutePositions()` 快照(在任何 mutation 之前)。
  2. **后半段**(全新 `BeltLine`,新 id):对每条 lane,`backPos = [p - (k+1)*L for p in abs if p >= (k+1)*L]`,`FromAbsolutePositions((n-1-k)*L, backPos)`。`Tiles = 原 Tiles[k+1 ..]`;登记进池得 `backId`;这些格子的 `_tiles` 全部重指向 `backId`。
  3. **前半段**(原线,id 不变):对每条 lane,分两轮从原线摘除物品——
     - 先 `while (lane.TryRemoveItemInRange((k+1)*L, 当前长度)) {}`:把已在第 2 步复制进后半段的物品从原线移走,**不计数**(它们是搬走,不是丢失)。
     - 再 `while (lane.TryRemoveItemInRange(k*L - (W-1), 当前长度)) count++;`:剩下前沿 `≥ k*L - (W-1)` 的都是被移格上的、或身体跨进被移格的物品,**丢弃并计数**。
     然后各 `ShrinkBack((n-k)*L)`,`Tiles.RemoveRange(k, n-k)`。
  4. `_tiles.Clear(x,y)`。

  端点分支同理只需一轮清除:`k==0` 清 `[0, L)`(出口前无更前的格子、后方物品身体够不到出口格,不必拓宽);`k==n-1` 清 `[(n-1)*L - (W-1), n*L)`(拓宽 `W-1` 收跨界物品)。这些都计数(全是丢弃)。

前半段 / 端点截短复用 `ShrinkBack`/`ShrinkFront`(id 稳定、tile 索引 churn 最小);只有中间拆分的后半段是全新线。拆分不在每 tick 热路径上,只在拆除时触发一次,不要求 O(1),只要求正确、可测。

## 7. 每 tick 系统更新

`Simulation.Step()` 在命令应用之后、`Tick++` 之前,**在 `Step` 里直接编排**两趟(不新增 `BeltNetwork.Advance` 方法;`BeltNetwork` 只把 `Delta(byte) → (dx,dy)` 暴露成 `public static`,其余靠 3b 已有的 `Capacity`/`IsAliveAtIndex`/`GetAtIndex`/`GetLineAt`/`GetLine`)。两趟都按 `BeltLine` 池的索引序(与 `WriteState` 遍历一致)。

**1. 推进**:每条存活线,`int speed = ResolveBeltSpeed(line);` 然后 `line.LaneA.Advance(speed)`、`line.LaneB.Advance(speed)`。

`ResolveBeltSpeed(BeltLine line)` 是 `Simulation` 的私有方法,按 `line.Tiles[0]`(出口格)→ `World.GetEntityAt` → `Entities.Get().ProtoId` → `Prototypes` → `((TransportBeltPrototype)p).SpeedSubTilesPerTick` 解析(严格,靠"出口格上一定是传送带实体"的不变式,不做 fallback)。沿用 Plan 2 的约束:该值应整除 `ItemWidthSubTiles`=64。合并不要求同 prototype(第 5 节),一条线可能跨越不同速度的格子——本设计的既定行为是**整条线按出口格速度跑**,不模拟"物品进入慢段减速"。精确分段速度留给分离器 / 多带种计划;M1 的基准与测试场景用单一带种,不触发这个近似。

**2. 线间交接(最简版)**:推进之后,再遍历每条线,解析它出口格 `Tiles[0]` 的"前方邻格" `front = Tiles[0] + BeltNetwork.Delta(line.Direction)`;`dId = Belts.GetLineAt(front)`;若 `dId` 有效、`down = Belts.GetLine(dId)` 满足 `down.Direction == line.Direction` 且 `down.Tiles[^1] == front`(`front` 正是下游线的入口端),则对 A/B 两条 lane 各做:
`while (line.LaneX.IsFrontReady && down.LaneX.TryInsertAtBack()) line.LaneX.RemoveFront();`
前方邻格只通过 `Belts.GetLineAt` 解析(tile 索引),不存下游引用(避免悬垂),不需要 `EntityId → 线`。`TryInsertAtBack` 自带 64 亚格间距校验:下游空带时一次 tick 可搬多个物品(符合"空带自由铺入"),下游满带时自然停在 `IsFrontReady`。

遍历序固定(池索引),所以完全确定。顺序确实影响"某物品这一 tick 还是下一 tick 过界"(链式 A→B→C 中,先处理 A 可能让一个物品一 tick 内连过两个边界,仅在带子近空时发生),但序固定 ⇒ 回放必然复现,不需要拓扑排序。真正的线尾(没有下游线,也还没有机械臂 / 箱子接入)物品停在 `gaps[0]==0`,`IsFrontReady` 就是等待信号。

**环形回路**就是这一步的一个闭合特例:矩形/任意闭环 = 若干直线 `BeltLine` 首尾相接成一圈(拐角格因方向不同各自成线),交接沿环一段段传。环没塞满时物品持续循环;环整圈塞满时,每条线出口都堵、前方线都收不下,整环冻住——`while` 条件自然给出这个结果,不需要额外的环检测。

## 8. `IStateWriter` 覆盖 + `Simulation` 接线(Plan 3d)

**`Simulation.WriteState`**:在 `WorldGrid` 那段之后加一行 `Belts.WriteState(writer);`。`BeltNetwork.WriteState`(3b 已实现)先写分配器簿记,再按池索引序写每条线的 `Direction`、`Tiles`(数量+每格坐标,确定顺序)、`LaneA`/`LaneB` 的 `WriteState`(第 3 节;gap 数量 + gap 列表,不含 `_openIndex`)。

**`Simulation` 其余接线**:

- 新增 `public BeltNetwork Belts { get; } = new();`
- `Apply` / `PlaceEntity`:`World.OccupyArea` 成功后,若 `proto is TransportBeltPrototype` → `Belts.AddBelt(command.X, command.Y, command.Rotation)`(返回值忽略;tile 索引与合并在 `AddBelt` 内部完成)。
- `Apply` / `RemoveEntity`:在 `Entities.Destroy(id)` **之前**把要用的东西抓出来——`(x, y) = (data.X, data.Y)`、`bool isBelt = proto is TransportBeltPrototype`(`data` 在 `Destroy` 后失效,不能事后再读);既有的 `World.ClearArea` + `Entities.Destroy` 照跑;然后若 `isBelt` → `Belts.RemoveBelt(x, y)`(返回的丢弃计数忽略——M1 无背包,第 10 节的策略层落地前实际效果就是丢弃)。belt 后处理排在既有实体移除逻辑之后(第 10 节)。
- `Step`:命令应用之后、`Tick++` 之前,执行第 7 节的两趟(推进 + 线间交接)。

`Command.Rotation`(0/1/2/3=北/东/南/西)直接作为 `AddBelt` 的 `direction`;`EntityData.Rotation` 已存这个值,无需改动。

## 9. 明确不做(留给更后续的计划)

- 机械臂的实际行为(够不够得着、抓取节奏、过滤器、增量追踪定位)——本设计只保证 `TryRemoveItemInRange` 这个原语存在,机械臂计划是纯粹的调用者。
- 玩家手动从传送带取物品的输入/UI/背包联动——同样只依赖 `TryRemoveItemInRange`,交互层不在本设计范围。
- 分离器(spec 5.2 提到的天然切分点)——分离器出现后,合并算法需要把"遇到分离器"也当作端点边界,这是后续计划的事,本设计的合并/拆分算法先只考虑"两个同向传送带直接相邻"这一种边界。
- 侧向汇入(sideloading:一条带垂直怼到另一条线的侧面喂入单侧 lane)——本设计中"贴在一条线中间而非端点"的新格一律落到"两边都接不上 → 新建单格线"。侧向汇入是后续计划。
- 混速合并线的精确减速——见第 7 节,本设计整条线按出口格速度跑,不模拟物品进入慢段减速。
- 单条 `BeltLine` 的环形数据结构(带 wraparound 的 lane)——算法层不需要,环形回路的游戏体验由第 7 节的直线段 + 线间交接提供。
- 传送带旋转导致的 footprint 宽高互换——Plan 2 已指出 `EntityData.Rotation` 存了但未使用,本设计的传送带固定 1×1,不涉及这个问题。

## 10. 风险与待确认点

- **移除的格子上卡着的物品去哪**:分两层。**结构层(Plan 3c,`BeltNetwork.RemoveBelt`)**:被移格及其两侧亚格边界内的物品,用 `TryRemoveItemInRange` 循环摘下并**计数**,`RemoveBelt` 把计数返回给调用者。3c 不决定去向,也不引入任何替代实体。**策略层(Plan 3d 及以后)**:`Simulation` 接入后才有"谁在拆"的上下文,那时再定去向——玩家手拆进背包、机器人拆进物流网、脚本清除直接丢弃等。当前代码库既没有玩家背包、`BeltLane` 上的物品也还没有类型(`TryInsertAtBack` 无参、`_gaps` 只是整数),所以在 3d/背包系统落地前,实际效果 = 丢弃。这个分层让 3d 改去向时不必碰 3c。
- **合并/拆分与 `WorldGrid` 的 footprint 占位如何配合**:传送带都是 1×1,`Simulation.Apply` 现有的 `PlaceEntity`/`RemoveEntity` 逻辑(占位检查、拒绝计数)不用改,合并/拆分是在这套既有逻辑成功执行**之后**追加的一步后处理。

## 11. 实施拆分成多个计划

延续 M1 Plan 1→Plan 2 的节奏,拆成几个顺序执行、各自独立可测的小计划:

- **Plan 3a**(已合并):`BeltLane` 新增能力(`ExtendBack`/`ExtendFront`/`ToAbsolutePositions`/`FromAbsolutePositions`/`TryRemoveItemInRange`/`WriteState`)+ 独立单测。
- **Plan 3b**(已合并):`BeltLineId` + `BeltLinePool`(引用类型池,分配器同 `EntityPool<T>` 思路,不改既有 struct 池)+ `BeltLine` 数据结构 + `BeltNetwork`(持有池 + `tile → BeltLineId` 分块索引)+ `AddBelt` 合并算法(单侧 / 三路 / 都不接)+ 独立单测。
- **Plan 3c**(已合并):`BeltLane.ShrinkBack`/`ShrinkFront`(第 3 节,含 fail-fast 守卫)+ `TileToLineIndex.Clear` + `BeltNetwork.RemoveBelt`(第 6 节四分支;端点与前半段就地 `Shrink`,后半段全新线;断口跨界物品清除范围向前拓宽 `W-1`;返回被清物品计数)+ 独立单测。
- **Plan 3d**:接入 `Simulation`(第 8 节)——`Belts` 属性;`Apply` 的 `PlaceEntity`/`RemoveEntity` 对传送带调 `AddBelt`/`RemoveBelt`;`Step` 里编排第 7 节的推进 + 线间交接(`BeltNetwork.Delta` 暴露为 `public static`,`ResolveBeltSpeed` 私有);`WriteState` 追加 `Belts.WriteState`。**不做** `EntityId → 线` 反查(3d 评审取消,见第 4 节)。测试:确定性(扩 `DeterminismTests`:放一串带子 + 中间拆一格 + 补一格触发合并 + 多 tick,两遍 hash 全等)、L 形拐弯两条线的物品交接集成测试、矩形环的循环与塞满冻结集成测试。(存档/读档后 `_openIndex` 归零仍哈希一致——已由 3a 的 `WriteState` 单测覆盖:堵塞态 lane vs `FromAbsolutePositions` 重建的 lane 哈希相等;M2 有真存读档时再补 `Simulation` 级往返测试。)

每个计划都走完整的"实现→审查→(修复→复审)"闭环,前一个合并进 `main` 后再规划下一个的具体任务清单(避免过早锁定后面计划的细节,给中途发现的问题留调整空间)。
