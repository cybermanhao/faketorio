using Faketorio.Sim.Commands;

namespace Faketorio.Sim.Bench.Scenario;

// 一整片供电区:横排 generatorCount 台 burner-generator(2×2),间距 GeneratorPitch(留 1 格空)。
// 发电机自己不并网——ElectricGrid 只按锚点格找网络,而发电机的 RegisterSupply 要求
// 锚点落在某根电线杆的供电区(Chebyshev ≤ 2)内。所以在这排发电机下方一行(y+2)
// 按"每 6 rel-x 一根"摆 small-electric-pole:
//   * 电线杆 rel-x ≡ 2 (mod 3),永远落在发电机 2×2 占的 rel-x∈{3i,3i+1} 之外,不撞;
//   * 相邻电线杆欧氏距离 6 < 7(maximumWireDistanceTiles),整排串成一张网;
//   * 每台发电机锚点 (3i, 0) 到最近电线杆 rel-x 差 ≤ 2、dy=2 -> Chebyshev 2,供给登记得上。
//
// 契约(见 IScenarioPart):Place 只 Submit,不 Step、不写库存。发电机的燃料缓冲
// (SetFuelBufferJ)由调用方在 drain-step 之后按 GeneratorTiles 注入。
//
// 坐标全部相对传入的 anchor:发电机锚点 (anchorX + 3i, anchorY),电线杆 (anchorX + px, anchorY + 2)。
public sealed class PowerDistrictPart : IScenarioPart
{
    // 发电机 2×2,3 格一台(1 格空)。
    public const int GeneratorPitch = 3;

    // 电线杆相对发电机排的行偏移,以及沿排的起始/步进(rel-x)。
    private const int PoleRowDy = 2;
    private const int PoleStartDx = 2;
    private const int PoleStepDx = 6;

    private readonly ScenarioProtoIds _ids;
    private readonly int _generatorCount;
    private readonly List<(int X, int Y)> _generatorTiles = new();

    public PowerDistrictPart(ScenarioProtoIds ids, int generatorCount)
    {
        if (generatorCount < 1)
            throw new ArgumentOutOfRangeException(nameof(generatorCount), generatorCount, "至少 1 台发电机");
        _ids = ids;
        _generatorCount = generatorCount;
    }

    // 最近一次 Place 放下的发电机锚点格,供 ScenarioBuilder / 测试在 drain-step 之后
    // 逐台 SetFuelBufferJ。顺序 = 提交顺序(从西到东)。
    public IReadOnlyList<(int X, int Y)> GeneratorTiles => _generatorTiles;

    public ScenarioPartFacts Place(Simulation sim, int anchorX, int anchorY)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int placed = 0;

        void Emit(string type, int protoId, int x, int y)
        {
            sim.Submit(new Command
            {
                Type = CommandType.PlaceEntity,
                ProtoId = protoId,
                X = x, Y = y, Rotation = 0,
            });
            placed++;
            counts[type] = counts.TryGetValue(type, out var c) ? c + 1 : 1;
        }

        // --- 发电机排(从西到东) --------------------------------------------
        _generatorTiles.Clear();
        for (int i = 0; i < _generatorCount; i++)
        {
            int gx = anchorX + i * GeneratorPitch;
            int gy = anchorY;
            Emit("burner-generator", _ids.Generator, gx, gy);
            _generatorTiles.Add((gx, gy));
        }

        // --- 电线杆:每 PoleStepDx 一根,直到最右一根覆盖最后一台发电机锚点 ----
        int lastGenDx = (_generatorCount - 1) * GeneratorPitch;
        for (int px = PoleStartDx; ; px += PoleStepDx)
        {
            Emit("small-electric-pole", _ids.Pole, anchorX + px, anchorY + PoleRowDy);
            if (px + _ids.PoleSupplyAreaDistanceTiles >= lastGenDx) break;
        }

        return new ScenarioPartFacts(
            EntitiesPlaced: placed,
            EntityCountByType: counts,
            RatedPowerSupplyJPerTick: _generatorCount * _ids.GeneratorOutputJPerTick,
            RatedPowerDemandJPerTick: 0,
            MinableOreUnderDrills: 0,
            SeededIronOre: 0,
            FedDrillCount: 0);
    }
}
