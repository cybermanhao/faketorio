# M1 Plan 6 — 矿脉生成(ResourceGrid + 定点值噪声)设计

日期: 2026-09-04
状态: 已与用户确认的设计基线
前置依赖: 无(路线图 P6 零前置)。与 M1 Plan 1(模拟核心)、Plan 3(传送带)、Plan 4(库存)平级,均已合并进 `main`
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(5.1 世界与实体存储、铁律 3 确定性)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(P6 条目)

## 1. 目标

给模拟层一个**惰性无限、种子确定的矿脉层**:定点值噪声场生成矿脉分布,`ResourceGrid` 分块存储可变矿量,`GetResourceAt` / `Extract` 查询与开采。这是 P5 手挖(`HandMine` 的"脚下有没有矿")和 P10 电力采矿机的数据地基——当前 sim 完全没有资源层,`items.json` 只有 `iron-ore`/`iron-plate`/`coal`/`wooden-chest`,没有任何矿脉概念。

同时把 `DeterministicHash` + `ValueNoise` 做成**独立可复用原语**:未来的水域、树木、地形高程都套同一套噪声,但各有各的存储与生命周期(见 §8)。

**不做**:挖矿命令 / 手挖(P5)、电力采矿机(P10)、矿脉表现层渲染、树木、水域、地形高程、生物群系、domain warp / ridged 变换、矿脉"好玩"调参(M1 只要确定性 + 自举可行)、`System.Random`。

## 2. 组件与文件结构

**新文件**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim/World/DeterministicHash.cs` | `(long seed, int field, int x, int y) → ulong` 整数混合哈希(splitmix64 式,常量钉死)。矿脉系统唯一的熵源。 |
| `sim/Faketorio.Sim/World/ValueNoise.cs` | 定点值噪声:格点值、smoothstep 缓动、双线性插值、多倍频 fBm;`isqrt` 整数平方根。全 Q16.16 整数 / `long`,无 `float`。纯函数。 |
| `sim/Faketorio.Sim/World/ResourceCell.cs` | `readonly record struct ResourceCell(int ResourceProtoId, int Amount)`,`Empty` / `IsEmpty`,镜像 `ItemStack`。 |
| `sim/Faketorio.Sim/World/ResourceGrid.cs` | 分块存储(自己的 `Dictionary<long, ResourceChunk>`,32×32,复用 `WorldGrid` 的 chunk 键与排序键约定)、chunk 首次触碰时惰性生成、`GetResourceAt` / `Extract` / `WriteState`。含内部 `ResourceChunk`。 |
| `sim/Faketorio.Sim/Prototypes/ResourcePrototype.cs` | `ResourcePrototype : PrototypeBase`、`MapGenPrototype : PrototypeBase`(单例)、`NoiseLayer` record、`StarterPatch` record。 |
| `data/base/resources.json` | coal / iron-ore / copper-ore / stone 四种矿的 prototype。 |
| `data/base/map-gen.json` | 单条 `map-gen`:全局默认格距 / 倍频数 + 启动矿斑列表。 |

**改动**

- `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs`:解析 `"resource"` 与 `"map-gen"` 两种 type + 校验。
- `data/base/items.json`:补 `copper-ore`、`stone` 两个 item。
- `sim/Faketorio.Sim/Simulation.cs`:构造函数加 `long worldSeed = 0`;`public ResourceGrid Resources { get; }`;`WriteState` 两处追加(见 §6)。**`Step` / `Apply` 不改。**

`ResourceGrid` 与 `WorldGrid` 是两个独立类,同样的 chunk 坐标但互不引用:`WorldGrid` 只管实体占用,`ResourceGrid` 只管矿层。一格上可以既有矿又有建筑(采矿机放在矿上——Factorio 语义)。

## 3. `DeterministicHash` + `ValueNoise`

### 3.1 `DeterministicHash`

```
public static class DeterministicHash
{
    static ulong Mix(ulong x)
    {
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }

    public static ulong Hash(long seed, int field, int x, int y)
    {
        ulong h = Mix((ulong)seed ^ 0x9E3779B97F4A7C15UL);
        h = Mix(h ^ ((ulong)(uint)field * 0x9E3779B97F4A7C15UL));
        h = Mix(h ^ ((ulong)(uint)x     * 0xFF51AFD7ED558CCDUL));
        h = Mix(h ^ ((ulong)(uint)y     * 0xC4CEB9FE1A85EC53UL));
        return h;
    }
}
```

全 `ulong` 位运算,C# 语义完全定义、跨平台位一致。`(uint)` 转换把负坐标 wrap 成确定的位模式。**禁用 `System.Random`**(.NET 版本间实现不稳定)。

### 3.2 `ValueNoise` —— 单倍频

`S0`(基础格距)是 2 的幂;倍频 `k` 的格距 `S = S0 >> k`,右移位数 `shift = log2(S0) - k`。要求 `S >= 1`(`Octaves <= log2(S0) + 1`,加载期校验)。

给定 tile `(tx, ty)`、seed、`field`:

1. 格点坐标 `lx0 = tx >> shift`(算术右移,负坐标向下取整),`lx1 = lx0 + 1`;`ly0`/`ly1` 同理。
2. 格点值 `LatticeQ16(seed, field, lx, ly) = (int)(DeterministicHash.Hash(seed, field, lx, ly) & 0xFFFF)` —— Q16.16 原始值,范围 `[0, 65535]`(即 `[0, 1)`)。四角 `v00 v10 v01 v11`。
3. 局部分数 `fx = tx & (S - 1)`(范围 `[0, S-1]`),转 Q16:`fxQ = (fx << 16) / S`(范围 `[0, 65536)`);`fyQ` 同理。
4. smoothstep 缓动 `ease(t) = t·t·(3 − 2t)`,Q16:
   `long t2 = ((long)t * t) >> 16;`
   `long e = (t2 * ((3L << 16) - 2L * t)) >> 16;`
   `ux = (int)ease(fxQ)`,`uy = (int)ease(fyQ)`。
5. 双线性:`lerp(p, q, t) = p + (int)(((long)(q - p) * t) >> 16)`;
   `a = lerp(v00, v10, ux)`;`b = lerp(v01, v11, ux)`;`octave = lerp(a, b, uy)` —— Q16 `[0, 1)`。

### 3.3 `ValueNoise` —— 多倍频 fBm

`K = Octaves`:
- `long sum = Σ_{k=0}^{K-1} (octave_k >> k);`
- `long norm = Σ_{k=0}^{K-1} (65536L >> k);`  // 直接循环求和,别用闭式——`K == 1` 时闭式 `(2<<16) - ((2<<16)>>(K-1))` 会得 0 除零。`K=1→65536`,`K=2→98304`,`K=3→114688`。
- `fbmQ = (int)((sum << 16) / norm);`  // Q16 `[0, 65536)`

`S0`、`K` 的来源见 §4:每个 `NoiseLayer` 可覆盖,缺省取 `MapGenPrototype` 的默认。

### 3.4 `isqrt`

`public static int Isqrt(long n)` —— 整数平方根(牛顿迭代或二分),`n < 0` 抛 `ArgumentOutOfRangeException`。启动矿斑的欧氏距离用它。放 `ValueNoise.cs`(或同目录 `MathI.cs`,实现者定)。

## 4. `ResourcePrototype` + `MapGenPrototype` + `NoiseLayer`

```
public sealed record NoiseLayer(
    int FieldId,
    int LatticeSize,     // S0,必须是 2 的幂
    int Octaves,         // K,要求 Octaves <= log2(LatticeSize) + 1
    int ThresholdQ16,    // 场值超过它这格才有该特征(Q16.16)
    bool Warp  = false,  // 预留,P6 不实现 —— 读到 true 抛 NotSupportedException
    bool Ridge = false);  // 同上

public sealed class ResourcePrototype : PrototypeBase
{
    public required string MinableResult { get; init; }  // 挖出来的 item name,如 "coal"
    public NoiseLayer Layer  { get; internal set; }       // 解析后的有效层;见 §4.1
    public int RichnessBase  { get; init; }              // 有矿时的最低矿量(>= 1)
    public int RichnessScale { get; init; }              // 额外矿量 = RichnessScale·excess >> 16
}

public sealed class MapGenPrototype : PrototypeBase      // 单例,约定 name = "default"
{
    public int DefaultLatticeSize { get; init; } = 64;
    public int DefaultOctaves     { get; init; } = 3;
    public IReadOnlyList<StarterPatch> StarterPatches { get; init; } = Array.Empty<StarterPatch>();
}

public readonly record struct StarterPatch(
    string Resource, int CenterX, int CenterY, int Radius, int CenterAmount);
```

### 4.1 层解析(loader 的 post-`AssignIds` pass)

JSON 的 `noise` 对象省略某字段 → `PrototypeLoader.Parse` 先把它存成哨兵 `0`。所有 prototype 注册 + `AssignIds()` 之后,`LoadFromDirectory` 跑一个**解析 + 校验 pass**:对每个 `ResourcePrototype`,读 `MapGenPrototype("default")`,把 `Layer` 重建为**全部字段具体**的 `NoiseLayer` 并写回(`Layer` 是 `internal set`,同 `PrototypeBase.Id`):

- `FieldId`:非 0 原样用(JSON 显式,权威);`== 0` → `1 + resourcePrototype.Id`(`Id` 此时已由 `AssignIds()` 赋值)。不同矿 → 不同 field → 独立噪声场。
- `LatticeSize` / `Octaves`:非 0 原样用;`== 0` → `MapGenPrototype.DefaultLatticeSize` / `DefaultOctaves`。

写回后立刻按 §4.3 校验**解析后的有效值**。`ResourceGrid` 直接读 `proto.Layer`,不再做任何解析或兜底——所有默认补齐与校验都在 loader 里,`PrototypeLoaderTests` 覆盖得到。

### 4.2 数据文件

`data/base/resources.json`:

```json
[
  { "type": "resource", "name": "coal",       "minableResult": "coal",
    "noise": { "thresholdQ16": 44000 }, "richnessBase": 400, "richnessScale": 6000 },
  { "type": "resource", "name": "iron-ore",   "minableResult": "iron-ore",
    "noise": { "thresholdQ16": 45000 }, "richnessBase": 400, "richnessScale": 6000 },
  { "type": "resource", "name": "copper-ore", "minableResult": "copper-ore",
    "noise": { "thresholdQ16": 45000 }, "richnessBase": 400, "richnessScale": 6000 },
  { "type": "resource", "name": "stone",      "minableResult": "stone",
    "noise": { "thresholdQ16": 46000 }, "richnessBase": 300, "richnessScale": 4000 }
]
```

`noise` 对象里省略的 `latticeSize` / `octaves` / `fieldId` → loader 存哨兵 `0`,post-`AssignIds` pass 补齐(§4.1)。

`data/base/map-gen.json`:

```json
[{ "type": "map-gen", "name": "default", "defaultLatticeSize": 64, "defaultOctaves": 3,
   "starterPatches": [
     { "resource": "coal",       "centerX":  6, "centerY": -8, "radius": 4, "centerAmount": 1500 },
     { "resource": "iron-ore",   "centerX": -9, "centerY": -6, "radius": 5, "centerAmount": 2000 },
     { "resource": "copper-ore", "centerX": -8, "centerY":  9, "radius": 4, "centerAmount": 1500 },
     { "resource": "stone",      "centerX":  9, "centerY":  7, "radius": 3, "centerAmount": 1000 }
   ]}]
```

`data/base/items.json` 追加:`{ "type": "item", "name": "copper-ore", "stackSize": 50 }`、`{ "type": "item", "name": "stone", "stackSize": 50 }`。

### 4.3 校验(post-`AssignIds` pass 里,抛 `InvalidDataException`,同 `ValidateFootprint` 风格)

针对**解析后的有效 `NoiseLayer`**:

- `map-gen` 出现 0 条或多于 1 条(这条在 pass 开头查)。
- 有效 `LatticeSize` 不是 2 的幂,或 `<= 0`。
- 有效 `Octaves <= 0`,或 `Octaves > log2(LatticeSize) + 1`(否则最细倍频格距 `< 1`)。
- `NoiseLayer.Warp` 或 `Ridge` 为 `true`(P6 不支持)。
- `StarterPatch.Resource` 查不到对应 `ResourcePrototype`;或 `Radius <= 0`;或 `CenterAmount <= 0`。
- `ResourcePrototype.MinableResult` 查不到对应 `ItemPrototype`。
- `RichnessBase < 1`。

放在一个跨引用 pass(`registry` 全部注册 + `AssignIds()` 之后)——`PrototypeLoader.LoadFromDirectory` 在 `return registry;` 前调用。不依赖文件名加载顺序。此 pass 既解析(§4.1)又校验。

## 5. `ResourceGrid`

```
public sealed class ResourceGrid
{
    public ResourceGrid(long seed, PrototypeRegistry protos);

    public ResourceCell GetResourceAt(int x, int y);   // 触发所在 chunk 惰性生成
    public int Extract(int x, int y, int count);        // 返回实际取出量;扣到 0 清空该格
    public void WriteState(IStateWriter writer);

    // 测试 / 表现层
    public int GeneratedChunkCount { get; }
    public bool IsChunkGenerated(int x, int y);
}

// 内部;每 chunk 两个平行数组,行主序 32×32
sealed class ResourceChunk
{
    public readonly int[] TypeId = new int[WorldGrid.ChunkSize * WorldGrid.ChunkSize];  // 0 = 空
    public readonly int[] Amount = new int[WorldGrid.ChunkSize * WorldGrid.ChunkSize];
}
```

构造时:存 `_seed`;从 `protos` 取所有 `ResourcePrototype` 按 `Id` 升序存 `_resources`(决胜用)、取 `MapGenPrototype`("default")存 `_mapGen`。`ResourcePrototype.Layer` 已被 loader 的 post-`AssignIds` pass 解析成全部字段具体的 `NoiseLayer`(§4.1),`ResourceGrid` 直接用,不再兜底。

**`ResourceCell` 不变式**(同 `ItemStack`):没有代码路径产生 `TypeId != 0 && Amount == 0` —— §5.2 只在 `amount >= RichnessBase >= 1` 时落矿,§5.3 `patchAmount == 0` 视作不覆盖,§5.5 `Extract` 扣到 0 时整格 `TypeId` 归 0。所以 `cell == ResourceCell.Empty` ⟺ `cell.IsEmpty`(`IsEmpty => Amount == 0`)。

chunk 键、`ChunkKey(x,y)`、`TileIndex(x,y)`、排序键懒排序:与 `WorldGrid` 完全相同的实现(可以把这几个 `static` 方法提到一个共享 `ChunkMath` 里,或在 `ResourceGrid` 里复制——实现者定;复制的话 spec §7 的"同约定"要在测试里锚定)。

### 5.1 惰性生成(`GetOrGenerateChunk`,chunk 首次被 `GetResourceAt` / `Extract` 触碰)

1. 新建 `ResourceChunk`(`TypeId` / `Amount` 全 0),放进 `_chunks`,`_keysDirty = true`。
2. 遍历该 chunk 的 1024 格 `(x, y)`,每格算 §5.2 的矿种判定 → 写 `(TypeId[i], Amount[i])` 或留 0。
3. 叠加启动矿斑(§5.3),覆盖噪声结果。
4. chunk 完全独立:每格只是 `(seed, x, y)` 的纯函数,无邻块依赖。

### 5.2 每格矿种判定

对每个 `r in _resources`:
- `v = ValueNoise.Fbm(seed, r.Layer.FieldId, x, y, r.Layer.LatticeSize, r.Layer.Octaves)` —— Q16 `[0, 65536)`。
- `excess = v - r.Layer.ThresholdQ16`;`excess <= 0` → 此矿在这格不存在。
- 归一化超出量 `score = (int)(((long)excess << 16) / (65536 - r.Layer.ThresholdQ16))`(让不同阈值公平比较)。

所有 `excess > 0` 的矿里取 `score` 最大者;平手取 `r.Id` 小的(`_resources` 已按 Id 升序,遍历时"严格大于才替换"即可)。选中矿 `r*`:
- `amount = r*.RichnessBase + (int)(((long)r*.RichnessScale * excess*) >> 16)`,其中 `excess*` 是 `r*` 的 `excess`;下限 `RichnessBase`(已 `>= 1`)。
- `TypeId[i] = r*.Id; Amount[i] = amount`。

没有任何矿 `excess > 0` → 该格留 0(空)。

### 5.3 启动矿斑叠加

对 chunk 里每格 `(x, y)`,按 `_mapGen.StarterPatches` **列表顺序**遍历:
- `dx = x - p.CenterX; dy = y - p.CenterY; dist = ValueNoise.Isqrt((long)dx*dx + (long)dy*dy);`
- `dist >= p.Radius` → 该矿斑不覆盖此格,继续下一个。
- `patchAmount = p.CenterAmount * (p.Radius - dist) / p.Radius`(整数除;`dist == Radius-?` 时线性降到接近 0;`patchAmount == 0` 视作不覆盖,`continue`)。
- 命中:`TypeId[i] = <p.Resource 的 proto Id>; Amount[i] = patchAmount;` 然后 **`break`**(列表靠前的矿斑胜,确定)。

启动矿斑与 seed 无关。只覆盖半径内的格;半径外保持 §5.2 的噪声结果。

### 5.4 `GetResourceAt(x, y)`

生成所在 chunk → `i = TileIndex(x, y)` → `TypeId[i] == 0` 返回 `ResourceCell.Empty`,否则 `new ResourceCell(TypeId[i], Amount[i])`。

### 5.5 `Extract(x, y, count)`

镜像 `Inventory.Remove` / 传送带丢弃计数:
1. `count <= 0` → 返回 0,不改状态(与 `Inventory.Insert`/`Remove` 同风格)。
2. 生成所在 chunk;`i = TileIndex(x, y)`;`TypeId[i] == 0`(空格)→ 返回 0。
3. `take = min(Amount[i], count); Amount[i] -= take;` `Amount[i] == 0` 时 `TypeId[i] = 0`(整格清空)。返回 `take`。

P6 里 `Extract` 没有调用者(挖矿是 P5/P10),但在 P6 直接测。

### 5.6 `WriteState`

`_sortedKeys`(懒排序)→ 逐 chunk `writer.Write(key)`,再逐格 `for i in [0,1024): writer.Write(TypeId[i]); writer.Write(Amount[i])`。只写已生成的块。

## 6. `Simulation` 接线

- 构造函数:`public Simulation(PrototypeRegistry prototypes, long worldSeed = 0)`。现有 `new Simulation(prototypes)` 全部照常编译。存 `_worldSeed` 字段。
- `public ResourceGrid Resources { get; }` = `new ResourceGrid(worldSeed, prototypes)`(在 `Belts` / `Inventories` 旁)。
- `WriteState`:
  - **开头**:`writer.Write(_worldSeed);`(在现有 `writer.Write(RejectedCommandCount);` 之后、`Entities.WriteState(writer);` 之前)。
  - **结尾**:`Belts.WriteState(writer);` → `Inventories.WriteState(writer);` → `Resources.WriteState(writer);`。
- `Step`、`Apply` **不改**——P6 无每 tick 逻辑、无命令。挖矿命令是 P5 / P10。

`worldSeed` 入哈希:两个不同 seed 的 sim 即使一个 chunk 都没生成,哈希也不同;存读档时 seed 跟着走。

## 7. 确定性 / `IStateWriter`

- `ResourceGrid.WriteState` 按排序键遍历,无 `Dictionary` 迭代;每格两个 `int`。`IStateWriter` 现有 `Write(int)` / `Write(long)` 够用,不加方法。
- 噪声全程 `long` / `int` + 位移,无 `float` / `double`;`DeterministicHash` 常量钉死;不用 `System.Random`。
- **惰性生成的确定性**:一个 chunk 的内容是 `(seed, cx, cy)` 的纯函数(§5.1 第 4 点)。**哪些** chunk 被生成 = 命令序列决定的查询集;命令相同,两遍必然生成同一批 chunk、同样内容。从没被查询的 chunk 不入哈希,无害。任一格的生成不依赖"当前生成的是第几个 chunk"或"其他 chunk 是否已生成"——因此访问顺序无关。
- 现有确定性测试是相对比较(两遍相等 / 变化前后不等)。`WriteState` 追加 `_worldSeed`(一个 `long`)+ 空 `Resources`(零个 chunk)不破坏它们——与 P4 追加 `Inventories` 同理。`DeterminismTests` 的 golden `RunScenario` / `RunBeltScenario` 不改动。

## 8. 未来层(树木 / 水域)—— 推迟,只铺路

**P6 代码不实现**;此节给下一个设计者铺路。

| | 矿层(P6 做) | 水域(以后) | 树木(以后) |
|---|---|---|---|
| 复用 | 定义 `ValueNoise` / `DeterministicHash` / `NoiseLayer` | 复用三者,再配一层 `NoiseLayer` | 同左 |
| 存储 | 每 chunk `(TypeId, Amount)` 数组 | 按需 `IsWater(x,y)`,或纯缓存位图(可丢弃) | 未定:实体 or 轻量层 |
| 可变性 | 挖一点少一点 | 不可变(M1 无填海) | 砍掉即消失 |
| 入 `WriteState` | 是(矿量是状态) | **否**(seed 的纯函数,存了是冗余) | 若走实体 → 已被实体状态覆盖 |
| 耦合 | 谁都不依赖,`Apply` 不碰 | 进 `WorldGrid.IsAreaFree` / `Simulation.Apply` 放置校验 | 挡建造 → 同水域 |
| 待定岔路 | — | 形状旋钮:大格距 + 中位阈值 + `Warp` | **树是实体还是轻量层?** 需自己一次 brainstorm |

形状差异化机制:每个 `NoiseLayer` 带自己的 `LatticeSize`(特征尺度)、`Octaves`、`ThresholdQ16`;`Warp`(domain warp,把圆团扭成有机形)、`Ridge`(`1 − |2v − 1|`,做河道 / 矿脉状)两个 `bool` P6 预留不实现。水域将来配 `{ latticeSize: 256~512, thresholdQ16: ~32768, warp: true }` 就出连片海岸线而非矿团。

## 9. 测试

- **`DeterministicHashTests`**:同输入同输出;任一参数(seed/field/x/y)变则输出变;若干已知输入的固定期望值(钉死回归,防止有人改了常量)。
- **`ValueNoiseTests`**:`Fbm` 多次调用一致;不同 seed / 不同 field → 场不同;结果落在 `[0, 65536)`;`Lerp` 在 t=0 / t=65536 / t=32768 的定点正确;`Ease(0)==0`、`Ease(65536)==65536`、`Ease` 单调不减;`Isqrt` 对 0、1、非完全平方(如 10→3)、大值、完全平方正确,负数抛。
- **`ResourceGridTests`**:
  - 生成一个 chunk,`GetResourceAt` 多次一致。
  - `Extract` 部分 / 全部 / 超量 / `count<=0`(不改状态)/ 空格(返回 0);扣到 0 整格 `TypeId` 归 0。
  - **访问顺序无关**:按顺序 A 查一批格 vs 顺序 B 查同一批 → `WriteState` 哈希相同。
  - 启动矿斑:不论 `seed`,原点附近预期坐标处有预期矿种、中心格矿量最高、`dist >= radius` 处不被该矿斑覆盖;列表靠前的矿斑在重叠处胜。
  - 不同 `seed` → 某个非启动区已知格的矿种 / 矿量不同(至少 `WriteState` 哈希不同)。
  - `WriteState`:相同生成块集 → 相同哈希;`Extract` 改了矿量 → 哈希变;从没查询的块不进哈希(`GeneratedChunkCount` 断言)。
- **`PrototypeLoaderTests`** 追加:`resources.json` / `map-gen.json` 正常加载;§4.3 每条校验各一个负例(非 2 的幂格距、`Warp:true`、starter patch 引用不存在的矿、多条 map-gen、`Octaves` 越界等)抛 `InvalidDataException`。
- **`SimulationTests`** 追加:`Resources` 属性存在;`GetResourceAt` 一格后 `IsChunkGenerated` 为真、`GeneratedChunkCount` +1;查询前后 `ComputeStateHash` 不同(`WriteState` 覆盖到了)。
- **`DeterminismTests`** 追加:
  - 同 `worldSeed` + 同命令(含若干 `sim.Resources.Extract(...)` 直接调用,仿 belt / inventory 场景的直接状态改动)跑两遍,逐 tick 哈希全等。
  - **不同 `worldSeed`** + 同命令 → 至少某 tick 哈希不同。
  - 现有 golden 场景仍过。

## 10. 实施拆分

- **Task 1**:`DeterministicHash` + `ValueNoise`(含 `Isqrt`)+ `DeterministicHashTests` + `ValueNoiseTests`。纯原语,不碰 prototype / Simulation。
- **Task 2**:`ResourcePrototype` + `MapGenPrototype` + `NoiseLayer` + `StarterPatch` + `ResourceCell` + `PrototypeLoader` 两个 `case` + post-`AssignIds` 解析&校验 pass(§4.1 / §4.3)+ `data/base/resources.json` / `map-gen.json` + `items.json` 补 `copper-ore` / `stone` + `PrototypeLoaderTests` 追加。不碰 Simulation。
- **Task 3**:`ResourceGrid` + 内部 `ResourceChunk`(惰性生成、§5.2 矿种判定、§5.3 启动矿斑叠加、`GetResourceAt` / `Extract` / `WriteState` / `GeneratedChunkCount` / `IsChunkGenerated`)+ `ResourceGridTests`。消费 Task 1 的 `ValueNoise` 与 Task 2 的已解析 `ResourcePrototype.Layer`。
- **Task 4**:`Simulation` 接线(`worldSeed` 参数、`_worldSeed` 字段、`Resources` 属性、`WriteState` 两处追加)+ `SimulationTests` / `DeterminismTests` 追加。

每个 Task 走完整"实现→审查→(修复→复审)"闭环。
