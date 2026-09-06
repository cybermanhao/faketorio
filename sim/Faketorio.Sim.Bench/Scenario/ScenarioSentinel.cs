namespace Faketorio.Sim.Bench.Scenario;

// 场景搭建失败(几何冲突 / 规模不符 / 电力不够 / 矿太薄)时抛这个。
// 基准跑分只有在场景本身"确实是那个场景"时才有意义——所以宁可炸,不出数。
public sealed class ScenarioSentinelException : Exception
{
    public ScenarioSentinelException(string message) : base(message) { }
}

// 建造完成、注入完成、Assert 之前的场景守卫。四条断言(spec §4.3),
// 任一条不过就抛,消息里点名是哪条。
public static class ScenarioSentinel
{
    // --- DefaultScale 下钉死的实测常量 ---------------------------------------
    // 这些数只在 ScenarioBuilder.WorldSeed / 几何常量 / data/base 都不变时成立。
    // 改任何一个都要重新实测填这里——这正是守卫的意义:悄悄漂了就炸。
    public const int DefaultScaleEntities = 12009;

    // 实测 fed drill 数(4 * DefaultScale = 800 台采矿机里脚下有铁矿的台数)。
    public const int MeasuredFedDrillCount = 137;

    // 下界 = 实测值的 90%。噪声层是 seed 的纯函数,理论上一格不差;留 10%
    // 是给 data/base 里矿脉阈值的微调留余量,不是给"场景变了"留余量。
    public const int DefaultScaleFedDrillFloor = MeasuredFedDrillCount * 9 / 10;

    // DefaultScale 下各类型实体数(实测钉死)。
    public const int DefaultScaleDrills = 800;
    public const int DefaultScaleFurnaces = 400;
    public const int DefaultScaleBelts = 3200;
    public const int DefaultScaleInserters = 1200;
    public const int DefaultScaleWoodenChests = 800;
    public const int DefaultScaleLargeChests = 400;
    public const int DefaultScaleGenerators = 1269;
    public const int DefaultScalePoles = 3940;

    // 一台采矿机在 DefaultTicks 内、满 satisfaction 下最多能挖走的矿数:
    // ResourcePrototype.MiningTimeTicks = 60,MiningSpeed = 1.0 -> 每 60 tick 1 个。
    //
    // 【与 task brief 的偏差,故意的】brief 写的是
    //     totals.MinableOre > totals.FedDrillCount * DefaultTicks
    // 即"每台 fed drill 脚下要有 20000 个矿"。这在 data/base 下算术上不可能:
    // richnessBase 400 + richnessScale 6000 * excess,单格上限 ~2280,2x2 上限
    // ~9120。真正想表达的是"矿量不是瓶颈,基准跑的是机器不是空转",所以这里
    // 用真实的采矿速率上界 DefaultTicks / 60 = 333 作下界。
    public const long MaxOrePerDrillOverDefaultTicks = ScenarioBuilder.DefaultTicks / 60;

    public static void Assert(int scale, Simulation sim, ScenarioTotals totals)
    {
        // --- 0. 建造期没有一条命令被拒 -----------------------------------
        if (sim.RejectedCommandCount != 0)
            throw new ScenarioSentinelException(
                $"sentinel #0(无拒绝命令):建造期有 {sim.RejectedCommandCount} 条命令被拒(占位冲突)");

        // --- 1. 实体规模与公式一致 ---------------------------------------
        int expected = ScenarioBuilder.ExpectedEntities(scale);
        if (totals.Entities != expected)
            throw new ScenarioSentinelException(
                $"sentinel #1(实体规模):scale={scale} 实放 {totals.Entities} 个实体," +
                $"ExpectedEntities 公式算出 {expected}");

        if (scale == ScenarioBuilder.DefaultScale)
        {
            if (totals.Entities != DefaultScaleEntities)
                throw new ScenarioSentinelException(
                    $"sentinel #1(实体规模):DefaultScale 实体数 {totals.Entities} != 钉死常量 {DefaultScaleEntities}");

            AssertType(totals, "electric-mining-drill", DefaultScaleDrills);
            AssertType(totals, "stone-furnace", DefaultScaleFurnaces);
            AssertType(totals, "transport-belt-basic", DefaultScaleBelts);
            AssertType(totals, "inserter-basic", DefaultScaleInserters);
            AssertType(totals, "wooden-chest", DefaultScaleWoodenChests);
            AssertType(totals, "large-chest", DefaultScaleLargeChests);
            AssertType(totals, "burner-generator", DefaultScaleGenerators);
            AssertType(totals, "small-electric-pole", DefaultScalePoles);
        }

        // --- 2. 采矿机确实有矿可挖,且矿量不是瓶颈 -------------------------
        int fedFloor = ExpectedFedDrillCount(scale);
        if (totals.FedDrillCount < fedFloor)
            throw new ScenarioSentinelException(
                $"sentinel #2(采矿机有矿):scale={scale} fed drill {totals.FedDrillCount} < 下界 {fedFloor}" +
                $"(世界种子 {ScenarioBuilder.WorldSeed} 下的矿脉分布变了?)");

        // 每台 fed drill 脚下至少一格矿,而 data/base 的 richnessBase = 400 > 333,
        // 所以只要 fed drill 存在,这条在任何 scale 下都该成立。
        long oreFloor = (long)totals.FedDrillCount * MaxOrePerDrillOverDefaultTicks;
        if (totals.FedDrillCount > 0 && totals.MinableOre <= oreFloor)
            throw new ScenarioSentinelException(
                $"sentinel #2(矿量充裕):脚下可采矿 {totals.MinableOre} <= {oreFloor}" +
                $"({totals.FedDrillCount} 台 fed drill 在 {ScenarioBuilder.DefaultTicks} tick 内的采掘上限)," +
                "基准后半程会退化成空转");

        // --- 3. 额定供给盖得住额定需求 ------------------------------------
        if (totals.RatedSupply < totals.RatedDemand)
            throw new ScenarioSentinelException(
                $"sentinel #3(电力):额定供给 {totals.RatedSupply} < 额定需求 {totals.RatedDemand} J/tick");
    }

    // fed drill 下界。DefaultScale 用实测钉死值的 90%;其它 scale 不设下界
    // ——小阵列落在矿脉分布的哪一块纯看运气,钉不住(spec §4.3 也只钉 DefaultScale)。
    public static int ExpectedFedDrillCount(int scale)
        => scale == ScenarioBuilder.DefaultScale ? DefaultScaleFedDrillFloor : 0;

    private static void AssertType(ScenarioTotals totals, string type, int expected)
    {
        totals.CountByType.TryGetValue(type, out int actual);
        if (actual != expected)
            throw new ScenarioSentinelException(
                $"sentinel #1(实体构成):DefaultScale 下 {type} 有 {actual} 个 != 钉死常量 {expected}");
    }
}
