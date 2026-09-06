using Faketorio.Sim.Commands;

namespace Faketorio.Sim.Bench.Scenario;

// 一个自给自足的"铁生产单元":4 台电力采矿机(各自把矿塞进自己的木箱)+
// 一条 铁料源 -> 机械臂 -> 水平带 -> 拐角 -> 竖直带 -> 机械臂 -> 熔炉 x2 ->
// 机械臂 -> 输出带 -> 机械臂 -> 输出大箱 的冶炼链 + 覆盖全部用电实体的电线杆链。
//
// 契约(见 IScenarioPart):Place 只 Submit,不 Step、不写库存。实体在排掉
// PlaceEntity 命令的那次 Step 之前根本不存在,所以这里既读不到 EntityId 也
// 读不到库存。铁料源的 Inventory.Insert 和发电机的 SetFuelBufferJ 由调用方
// (ScenarioBuilder / 测试)在 drain-step 之后按 facts 执行。
//
// 坐标全部相对 anchor (ax, ay),严格落在 [ax, ax+CellWidth) x [ay, ay+CellHeight) 内。
// 方向常量与 BeltNetwork.Delta 一致:0=北(0,-1) 1=东(1,0) 2=南(0,+1) 3=西(-1,0)。
//
// ---------------------------------------------------------------------------
// 实测确定的最终几何(相对 anchor;所有值都在真实 sim 上跑通过 IronUnit_ProducesPlates):
//
//   采矿链(y 0..2)
//     drill d0..d3  (0,0) (4,0) (8,0) (12,0)  rot=2(南)  2x2
//       产出格 = (x, y+2) -> wooden-chest (0,2) (4,2) (8,2) (12,2)
//
//   冶炼链
//     铁料源 large-chest (2x3) @ (16,0)          占 x16..17, y0..2
//     insA (18,1) rot=1(东)   pickup (17,1)=铁料源  dropoff (19,1)=水平带
//     水平带(东) (19,1) (20,1) (21,1)            线出口 Tiles[0] = (21,1)
//     竖直带(南) (22,1)..(22,6)                  线入口 Tiles[^1] = (22,1)
//       >>> 拐角:水平带出口 (21,1)+Delta(东) = (22,1) 命中竖直带 Tiles[^1] -> 线间交接 <<<
//     insB (23,2) rot=1  pickup (22,2)=竖直带  dropoff (24,2)=f0(role 1 输入)
//     insC (23,5) rot=1  pickup (22,5)=竖直带  dropoff (24,5)=f1(role 1 输入)
//     熔炉 f0 (2x2) @ (24,1)   占 x24..25, y1..2
//     熔炉 f1 (2x2) @ (24,4)   占 x24..25, y4..5
//       (机械臂的取/放格解析只看 World.GetEntityAt,熔炉 2x2 的任一格都解析到
//        同一个 EntityId;role 由 CraftingMachinePrototype 判定:取=2 放=1。
//        所以"熔炉的哪一格"无所谓,只要挨着机械臂。)
//     insD (26,2) rot=1  pickup (25,2)=f0(role 2 输出)  dropoff (27,2)=输出带
//     insE (26,5) rot=1  pickup (25,5)=f1(role 2 输出)  dropoff (27,5)=输出带
//     输出带(南) (27,2)..(27,8)                  线出口 Tiles[0] = (27,8)
//     insF (27,9) rot=2(南)  pickup (27,8)=输出带出口  dropoff (27,10)=输出大箱
//     输出 large-chest (2x3) @ (27,10)           占 x27..28, y10..12
//
//   电线杆(供电区 Chebyshev<=2,连线距离<=7;只有 drill/furnace/inserter 耗电,
//   带子和箱子不耗;ElectricGrid 只按实体的锚点格找网络,所以覆盖锚点格即可)
//     (1,2) (5,2) (9,2) (13,2)  -> 覆盖 d0..d3 的锚点 (0,0)(4,0)(8,0)(12,0)
//     (17,3)                    -> 覆盖 insA (18,1)
//     (23,3)                    -> 覆盖 insB(23,2) insC(23,5) f0(24,1) f1(24,4)
//     (26,3)                    -> 覆盖 insD (26,2) insE (26,5)
//     (26,7)                    -> 覆盖 insF (27,9)
//     (31,4)                    -> 只为链接:让东边相邻单元的 (ax+36+1, ay+2)
//                                  号杆落在连线距离 7 之内(dx=6, dy=-2 -> 6)
//   杆链连通性(欧氏 isqrt):(1,2)-(5,2)=4 (5,2)-(9,2)=4 (9,2)-(13,2)=4
//     (13,2)-(17,3)=4 (17,3)-(23,3)=6 (23,3)-(26,3)=3 (26,3)-(26,7)=4 (26,7)-(31,4)=5
//   西侧接入点是 (1,2):外部电站的杆子只要落在它 7 格连线距离内即可并网。
// ---------------------------------------------------------------------------
public sealed class IronUnitPart : IScenarioPart
{
    public const int CellWidth = 36;
    public const int CellHeight = 32;
    public const long SeedIronOrePerUnit = 1500;

    // 一个单元固定提交的 PlaceEntity 条数:
    //   4 drill + 2 furnace + 4 wooden-chest + 2 large-chest + 6 inserter
    // + 16 belt(3 水平 + 6 竖直 + 7 输出) + 9 pole = 43
    // 改几何就要同步改这个常量(ScenarioBuilder 用它预估规模)。
    public const int EntitiesPerUnit = 43;

    private const int DrillCount = 4;
    private const int FurnaceCount = 2;
    private const int InserterCount = 6;

    private const byte North = 0, East = 1, South = 2;

    private readonly ScenarioProtoIds _ids;

    public IronUnitPart(ScenarioProtoIds ids) => _ids = ids;

    // --- 冶炼链的行/列锚点(相对 anchor)。几何只从这一块常量里调 ------------
    // 相邻关系是硬约束,改的时候要一起改:
    //   FurnaceX == FeedInserterX + 1        (上料机械臂 dropoff 落在熔炉西列)
    //   FeedInserterX == VBeltX + 1          (上料机械臂 pickup 落在竖直带)
    //   UnloadInserterX == FurnaceX + 2      (下料机械臂 pickup 落在熔炉东列)
    //   OutBeltX == UnloadInserterX + 1      (下料机械臂 dropoff 落在输出带)
    //   VBeltX == HBeltEndX + 1 && VBeltTopY == HBeltY   (拐角交接)
    private const int SrcChestX = 16, SrcChestY = 0;          // 铁料源 large-chest(2x3)
    private const int FeedBeltInserterX = 18;                 // insA:铁料源 -> 水平带
    private const int HBeltY = 1;                             // 水平带所在行
    private const int HBeltStartX = 19, HBeltEndX = 21;       // 水平带(东)首/末格,末格即线出口
    private const int VBeltX = 22;                            // 竖直带所在列
    private const int VBeltTopY = 1, VBeltBottomY = 6;        // 竖直带(南)入口/出口行
    private const int FeedInserterX = 23;                     // insB/insC 所在列
    private const int FurnaceX = 24;                          // 两台熔炉的锚点列(2x2 占 24..25)
    private const int Furnace0Y = 1, Furnace1Y = 4;           // 两台熔炉的锚点行
    private const int Furnace0RowY = 2, Furnace1RowY = 5;     // 机械臂对准的那一行(熔炉的第二行)
    private const int UnloadInserterX = 26;                   // insD/insE 所在列
    private const int OutBeltX = 27;                          // 输出带所在列
    private const int OutBeltTopY = 2, OutBeltBottomY = 8;    // 输出带(南)入口/出口行
    private const int OutInserterX = 27, OutInserterY = 9;    // insF:输出带出口 -> 输出箱
    private const int OutChestX = 27, OutChestY = 10;         // 输出 large-chest(2x3)

    // 单元里"铁料源"大箱的锚点格。ScenarioBuilder 在 drain-step 之后按
    // facts.SeededIronOre 往这里注入铁矿。
    public static (int X, int Y) IronSourceChestTile(int ax, int ay) => (ax + SrcChestX, ay + SrcChestY);

    // 单元里输出大箱的锚点格。sentinel / 测试从这里读铁板产量。
    public static (int X, int Y) OutputChestTile(int ax, int ay) => (ax + OutChestX, ay + OutChestY);

    // 一个单元满载时的额定电力需求(与坐标无关)。带子/箱子/电线杆不耗电。
    public static long PerUnitRatedDemand(ScenarioProtoIds ids)
        => DrillCount * ids.DrillEnergyJPerTick
         + FurnaceCount * ids.FurnaceEnergyJPerTick
         + InserterCount * ids.InserterEnergyJPerTick;

    // 采矿机锚点(相对 anchor)。矿只在采矿机自己的 2x2 脚下才会被采到
    // (Simulation.MiningDrillTickPreSettle 的目标搜索只扫自己的 footprint)。
    private static readonly (int Dx, int Dy)[] DrillAnchors =
    {
        (0, 0), (4, 0), (8, 0), (12, 0),
    };

    private static readonly (int Dx, int Dy)[] PoleAnchors =
    {
        (1, 2), (5, 2), (9, 2), (13, 2), (17, 3), (23, 3), (26, 3), (26, 7), (31, 4),
    };

    public ScenarioPartFacts Place(Simulation sim, int ax, int ay)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int placed = 0;

        void Emit(string type, int protoId, int x, int y, byte rot = 0)
        {
            sim.Submit(new Command
            {
                Type = CommandType.PlaceEntity,
                ProtoId = protoId,
                X = x, Y = y, Rotation = rot,
            });
            placed++;
            counts[type] = counts.TryGetValue(type, out var c) ? c + 1 : 1;
        }

        // --- 采矿链:4 台采矿机(朝南)+ 各自的木箱 ---------------------------
        long minableOre = 0;
        int fedDrills = 0;
        for (int i = 0; i < DrillAnchors.Length; i++)
        {
            var (dx, dy) = DrillAnchors[i];
            int x = ax + dx, y = ay + dy;
            Emit("electric-mining-drill", _ids.Drill, x, y, South);
            Emit("wooden-chest", _ids.WoodenChest, x, y + 2);   // 产出格 = 锚点 + Delta(南)*TileHeight

            // ResourceGrid 是惰性 chunk 生成的纯函数层,放实体之前就能查。
            long here = 0;
            for (int cell = 0; cell < 4; cell++)
            {
                var res = sim.Resources.GetResourceAt(x + cell % 2, y + cell / 2);
                if (!res.IsEmpty && res.ResourceProtoId == _ids.IronOreResource) here += res.Amount;
            }
            if (here > 0) fedDrills++;
            minableOre += here;
        }

        // --- 冶炼链 ---------------------------------------------------------
        var (srcX, srcY) = IronSourceChestTile(ax, ay);
        Emit("large-chest", _ids.LargeChest, srcX, srcY);

        Emit("inserter-basic", _ids.Inserter, ax + FeedBeltInserterX, ay + HBeltY, East);   // insA:铁料源 -> 水平带

        for (int x = HBeltStartX; x <= HBeltEndX; x++)                   // 水平带(东)
            Emit("transport-belt-basic", _ids.Belt, ax + x, ay + HBeltY, East);
        for (int y = VBeltTopY; y <= VBeltBottomY; y++)                  // 竖直带(南),拐角在 (VBeltX, VBeltTopY)
            Emit("transport-belt-basic", _ids.Belt, ax + VBeltX, ay + y, South);

        Emit("inserter-basic", _ids.Inserter, ax + FeedInserterX, ay + Furnace0RowY, East);     // insB:竖直带 -> f0
        Emit("inserter-basic", _ids.Inserter, ax + FeedInserterX, ay + Furnace1RowY, East);     // insC:竖直带 -> f1
        Emit("stone-furnace", _ids.Furnace, ax + FurnaceX, ay + Furnace0Y);                     // f0
        Emit("stone-furnace", _ids.Furnace, ax + FurnaceX, ay + Furnace1Y);                     // f1
        Emit("inserter-basic", _ids.Inserter, ax + UnloadInserterX, ay + Furnace0RowY, East);   // insD:f0 -> 输出带
        Emit("inserter-basic", _ids.Inserter, ax + UnloadInserterX, ay + Furnace1RowY, East);   // insE:f1 -> 输出带

        for (int y = OutBeltTopY; y <= OutBeltBottomY; y++)              // 输出带(南)
            Emit("transport-belt-basic", _ids.Belt, ax + OutBeltX, ay + y, South);

        Emit("inserter-basic", _ids.Inserter, ax + OutInserterX, ay + OutInserterY, South);  // insF:输出带 -> 输出箱
        var (outX, outY) = OutputChestTile(ax, ay);
        Emit("large-chest", _ids.LargeChest, outX, outY);

        // --- 供电覆盖 -------------------------------------------------------
        for (int i = 0; i < PoleAnchors.Length; i++)
        {
            var (px, py) = PoleAnchors[i];
            Emit("small-electric-pole", _ids.Pole, ax + px, ay + py);
        }

        return new ScenarioPartFacts(
            EntitiesPlaced: placed,
            EntityCountByType: counts,
            RatedPowerSupplyJPerTick: 0,
            RatedPowerDemandJPerTick: PerUnitRatedDemand(_ids),
            MinableOreUnderDrills: minableOre,
            SeededIronOre: SeedIronOrePerUnit,
            FedDrillCount: fedDrills);
    }
}
