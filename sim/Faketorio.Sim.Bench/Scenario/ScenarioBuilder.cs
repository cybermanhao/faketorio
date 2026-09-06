using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Bench.Scenario;

// CI 基准场景的搭建器:把 scale 个 IronUnitPart 铺成方阵,配一片够用的
// PowerDistrictPart,用一条 PoleBackbonePart 主干把两者以及各行单元串成一张电网,
// 做唯二的建造期注入(发电机燃料 + 单元铁料源),最后过 ScenarioSentinel。
//
// ---------------------------------------------------------------------------
// 几何总览(x 向东为正,y 向南为正;单元阵列锚在 (0,0) 向东南展开)
//
//   y = DistrictBaseY            ┌─ 电力区第 0 行:64 台 burner-generator 横排 + 自带杆链
//   y = DistrictBaseY + 5        ├─ 第 1 行 …                       (DistrictRowPitch = 5)
//   …                            └─ 第 districtRows-1 行(可能不满 64 台)
//
//   x = SpineX = -2              竖直主干(骨干 part 的第一段):从电力区顶行一路南下到 y=-2。
//                                电力区各行的杆列在 x=2,dx=4、dy≤3 -> 连线距离 5 ≤ 7,行行并网。
//
//   y = -2, 34, 70, …            每行单元上方 2 格的"肋":骨干横穿整行。
//     (RibY(r) = r*PitchY - 2)   单元西侧接入杆在 (ax+1, ay+2),dy=4、dx≤3 -> 距离 ≤5,行内每个单元并网。
//                                单元自身内容只占 ay+0..ay+12,肋走 ay-2 永远在净空里。
//
//   骨干走蛇形:肋 0 向东 -> 在 x=RibEastX 下降 PitchY -> 肋 1 向西 -> 在 x=SpineX 下降 -> …
//   下降段所在的两列(x=-2 与 x=RibEastX)都在单元内容之外,不会撞。
//
// 【为什么每一段的长度都必须是 6 的倍数】
//   PoleBackbonePart 的铺杆判据是"离上一根 Chebyshev ≥ 6 就放一根"。在拐角处
//   Chebyshev 会低估欧氏距离:上一根在拐角前 5 格、拐过弯再走 6 格,Chebyshev=6
//   触发放杆,但欧氏 sqrt(25+36)=7.8 > 7 = maximumWireDistanceTiles,链就断了。
//   把每段长度取成 6 的倍数,杆必然正好落在每个拐角上、相位在拐角处归零,
//   于是全程相邻杆都是"同一条直线上相距 6",欧氏距离恒为 6。
//   ExpectedEntities 的骨干杆数公式(总步数 / 6 + 1)也依赖这一点。
// ---------------------------------------------------------------------------
public static class ScenarioBuilder
{
    // 世界种子 + 单元阵列原点。Task 5 实测选定:10 个候选种子 x 42 个原点偏移
    // 全扫一遍(探针表见 task-5-report.md),这一组给出 RejectedCommandCount == 0
    // 且 fed drill 占比最高的一档 —— scale 50 下 200 台采矿机里 26 台脚下有铁矿(~13%)。
    //
    // 【为什么这么低】铁矿是 fbm 噪声层,thresholdQ16 = 45000 且要和 coal /
    // copper / stone 三层抢格子,实测全局铁矿覆盖率只有 ~11%;又因为噪声
    // latticeSize = 64 而一个单元的 4 台采矿机挤在 14x2 格里,4 台几乎总是同生共死,
    // "多摇几次骰子"的独立性红利拿不到。~13% 已经是这套几何能摸到的天花板。
    public const long WorldSeed = 20260905L;

    public const int DefaultScale = 50;
    public const int DefaultTicks = 5000;

    // --- 几何常量(全部在这里,几何代码里不出现裸数字) -----------------------
    // 原点不是 (0,0):它是探针选出来的"矿最肥的一块地"。所有其它坐标都相对它算。
    public const int UnitOriginX = 2048;
    public const int UnitOriginY = -1024;
    public const int UnitGapX = 4;
    public const int UnitGapY = 4;

    private const int PitchX = IronUnitPart.CellWidth + UnitGapX;    // 40
    private const int PitchY = IronUnitPart.CellHeight + UnitGapY;   // 36

    // 骨干竖直主干所在列 = 电力区西侧 2 格,也是单元阵列西侧 2 格。
    private const int SpineX = UnitOriginX - 2;
    // 肋相对单元行锚点的 y 偏移(负 = 在单元上方的净空里)。
    private const int RibDy = -2;
    // 肋向东至少要越过最后一列单元的东边界多少格。
    private const int RibEastMargin = 34;

    private const int DistrictX = UnitOriginX;
    private const int GeneratorsPerDistrictRow = 64;
    private const int DistrictRowPitch = 5;          // 发电机 2 行 + 杆 1 行 + 2 行净空
    private const int DistrictClearanceY = 60;       // 电力区底边到肋 0 的最小净空

    // PoleBackbonePart 的铺杆间距(与其 MaxSpacingTiles 一致——公式要靠它)。
    private const int PoleSpacing = 6;

    // 发电机余量:额定供给要盖住额定需求,取整之外再多 2 台。
    private const int GeneratorMargin = 2;

    // 每台发电机注入的燃料缓冲:一满组煤(stackSize 50)。
    private const int FuelSeedCoalCount = 50;

    // --- data/base 的两个数值,ExpectedEntities 要在没有 sim 的情况下算规模,
    //     所以钉在这里;Build 会拿真实 prototype 校验,漂了就抛。 -------------
    private const long PerUnitRatedDemandJPerTick = 9498;
    private const long GeneratorOutputJPerTick = 1500;

    public static BuiltScenario Build(
        int scale, long seed = WorldSeed, Faketorio.Sim.Profiling.IStepProfiler? profiler = null)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale), scale, "至少 1 个单元");

        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), seed, profiler);
        var ids = new ScenarioProtoIds(sim.Prototypes);

        // 钉死的数值必须和真实 prototype 一致,否则 ExpectedEntities 的规模公式失效。
        long perUnitDemand = IronUnitPart.PerUnitRatedDemand(ids);
        if (perUnitDemand != PerUnitRatedDemandJPerTick)
            throw new ScenarioSentinelException(
                $"data/base 漂了:单元额定需求 {perUnitDemand} != 钉死常量 {PerUnitRatedDemandJPerTick} J/tick");
        if (ids.GeneratorOutputJPerTick != GeneratorOutputJPerTick)
            throw new ScenarioSentinelException(
                $"data/base 漂了:发电机输出 {ids.GeneratorOutputJPerTick} != 钉死常量 {GeneratorOutputJPerTick} J/tick");

        var layout = Plan(scale);

        // --- 1. 电力区:若干横排,自北向南 ---------------------------------
        var partFacts = new List<ScenarioPartFacts>();
        var generatorTiles = new List<(int X, int Y)>();
        for (int r = 0; r < layout.DistrictRows; r++)
        {
            int gens = GeneratorsInDistrictRow(layout, r);
            var pd = new PowerDistrictPart(ids, gens);
            partFacts.Add(pd.Place(sim, DistrictX, layout.DistrictBaseY + r * DistrictRowPitch));
            for (int g = 0; g < pd.GeneratorTiles.Count; g++) generatorTiles.Add(pd.GeneratorTiles[g]);
        }

        // --- 2. 单元方阵:行优先,自西向东 ---------------------------------
        var unitFacts = new List<ScenarioPartFacts>();
        var unitAnchors = new List<(int X, int Y)>();
        for (int u = 0; u < scale; u++)
        {
            int col = u % layout.Cols, row = u / layout.Cols;
            int ax = UnitOriginX + col * PitchX;
            int ay = UnitOriginY + row * PitchY;
            unitFacts.Add(new IronUnitPart(ids).Place(sim, ax, ay));
            unitAnchors.Add((ax, ay));
        }

        // --- 3. 骨干:竖直主干 + 蛇形肋 ------------------------------------
        var backboneFacts = new PoleBackbonePart(ids, BackboneWaypoints(layout)).Place(sim, 0, 0);

        // --- 4. drain:一次 Step 排掉全部 PlaceEntity --------------------------
        sim.Step();

        // --- 5. 建造期注入(唯二) -------------------------------------------
        long fuelSeedJ = FuelSeedCoalCount * ids.CoalFuelValueJ;
        for (int g = 0; g < generatorTiles.Count; g++)
        {
            var (gx, gy) = generatorTiles[g];
            sim.ElectricGrid.SetFuelBufferJ(sim.World.GetEntityAt(gx, gy), fuelSeedJ);
        }
        for (int u = 0; u < unitAnchors.Count; u++)
        {
            var (ax, ay) = unitAnchors[u];
            var (sx, sy) = IronUnitPart.IronSourceChestTile(ax, ay);
            var inv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(sx, sy)));
            inv.Insert(ids.IronOre, (int)IronUnitPart.SeedIronOrePerUnit, ids.IronOreStackSize);
        }

        // --- 6. 汇总 ---------------------------------------------------------
        partFacts.AddRange(unitFacts);
        partFacts.Add(backboneFacts);
        var totals = Aggregate(partFacts);

        ScenarioSentinel.Assert(scale, sim, totals);
        return new BuiltScenario(sim, unitFacts, totals);
    }

    // 纯公式:同一个 scale 下 Build 会放下的实体总数。
    // = 电力区(发电机 + 自带杆) + 单元 * EntitiesPerUnit + 骨干杆。
    public static int ExpectedEntities(int scale)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale), scale, "至少 1 个单元");
        var layout = Plan(scale);

        int district = 0;
        for (int r = 0; r < layout.DistrictRows; r++)
        {
            int gens = GeneratorsInDistrictRow(layout, r);
            district += gens + DistrictPoleCount(gens);
        }

        return district + scale * IronUnitPart.EntitiesPerUnit + BackbonePoleCount(layout);
    }

    // 骨干折线总步数。所有段都是轴对齐、长度为 PoleSpacing 的倍数,
    // 所以杆正好落在 0, 6, 12, … 步处(拐角必有杆),数量 = 总步数 / 6 + 1。
    private static int BackbonePoleCount(in Layout layout)
    {
        int spineLen = RibY(0) - (layout.DistrictBaseY + 2);
        int ribLen = layout.RibEastX - SpineX;
        int total = spineLen + layout.Rows * ribLen + (layout.Rows - 1) * PitchY;
        return total / PoleSpacing + 1;
    }

    private static List<(int X, int Y)> BackboneWaypoints(in Layout layout)
    {
        var wp = new List<(int X, int Y)>
        {
            (SpineX, layout.DistrictBaseY + 2),   // 电力区顶行的杆行高度
            (SpineX, RibY(0)),                    // 南下到肋 0 的西端
        };
        for (int r = 0; r < layout.Rows; r++)
        {
            int xEnd = r % 2 == 0 ? layout.RibEastX : SpineX;
            wp.Add((xEnd, RibY(r)));                       // 横穿本行
            if (r + 1 < layout.Rows) wp.Add((xEnd, RibY(r + 1)));   // 就地南下到下一行
        }
        return wp;
    }

    private static int RibY(int row) => UnitOriginY + row * PitchY + RibDy;

    // PowerDistrictPart 的杆数:px = 2, 8, 14, … 直到 px + supplyArea(2) >= 3*(gens-1)。
    private static int DistrictPoleCount(int gens)
    {
        int lastGenDx = (gens - 1) * PowerDistrictPart.GeneratorPitch;
        int n = 1;
        for (int px = 2; px + 2 < lastGenDx; px += 6) n++;
        return n;
    }

    private static int GeneratorsInDistrictRow(in Layout layout, int row)
        => row + 1 < layout.DistrictRows
            ? GeneratorsPerDistrictRow
            : layout.GeneratorCount - (layout.DistrictRows - 1) * GeneratorsPerDistrictRow;

    private readonly struct Layout
    {
        public readonly int Cols, Rows, GeneratorCount, DistrictRows, DistrictBaseY, RibEastX;

        public Layout(int cols, int rows, int generatorCount, int districtRows, int districtBaseY, int ribEastX)
        {
            Cols = cols; Rows = rows; GeneratorCount = generatorCount;
            DistrictRows = districtRows; DistrictBaseY = districtBaseY; RibEastX = ribEastX;
        }
    }

    private static Layout Plan(int scale)
    {
        int cols = (int)Math.Ceiling(Math.Sqrt(scale));
        int rows = (scale + cols - 1) / cols;

        int generatorCount = (int)Math.Ceiling(
            scale * (double)PerUnitRatedDemandJPerTick / GeneratorOutputJPerTick) + GeneratorMargin;
        int districtRows = (generatorCount + GeneratorsPerDistrictRow - 1) / GeneratorsPerDistrictRow;

        // 电力区整片的高度(最后一行的杆行为止),再留净空,放在单元阵列正北的负 y 区。
        int districtSpan = (districtRows - 1) * DistrictRowPitch + 3;
        int districtBaseY = UnitOriginY - (districtSpan + DistrictClearanceY);
        // 竖直主干段长必须是 6 的倍数(见文件头);不够就把电力区再往北挪。
        while ((RibY(0) - (districtBaseY + 2)) % PoleSpacing != 0) districtBaseY--;

        // 肋向东要越过最后一列单元,且肋长(RibEastX - SpineX)是 6 的倍数。
        int need = UnitOriginX + (cols - 1) * PitchX + RibEastMargin - SpineX;
        int ribEastX = SpineX + (need + PoleSpacing - 1) / PoleSpacing * PoleSpacing;

        return new Layout(cols, rows, generatorCount, districtRows, districtBaseY, ribEastX);
    }

    private static ScenarioTotals Aggregate(List<ScenarioPartFacts> facts)
    {
        int entities = 0, fed = 0;
        long supply = 0, demand = 0, ore = 0;
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            entities += f.EntitiesPlaced;
            fed += f.FedDrillCount;
            supply += f.RatedPowerSupplyJPerTick;
            demand += f.RatedPowerDemandJPerTick;
            ore += f.MinableOreUnderDrills;
            foreach (var kv in f.EntityCountByType)
                byType[kv.Key] = byType.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;
        }

        return new ScenarioTotals(entities, fed, supply, demand, ore, byType);
    }
}

// 搭好的场景。Sim 停在 tick 1(建造 drain 那一步),注入已完成,sentinel 已过。
public sealed record BuiltScenario(
    Simulation Sim,
    IReadOnlyList<ScenarioPartFacts> PartFacts,
    ScenarioTotals Totals);

public readonly record struct ScenarioTotals(
    int Entities,
    int FedDrillCount,
    long RatedSupply,
    long RatedDemand,
    long MinableOre,
    IReadOnlyDictionary<string, int> CountByType);
