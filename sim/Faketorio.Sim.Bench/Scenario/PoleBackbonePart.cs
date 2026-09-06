using Faketorio.Sim.Commands;

namespace Faketorio.Sim.Bench.Scenario;

// 一串把若干 waypoint 桥起来的 small-electric-pole。给定一条折线(相邻 waypoint
// 之间走直线),沿线每 ≤ MaxSpacingTiles 格放一根杆,使整串在电网里连成一张网
// (相邻杆欧氏距离 ≤ 6 < 7 = maximumWireDistanceTiles,留 1 格余量)。
//
// 竖排单元之间、发电区与单元之间不会自动并网(ElectricGrid 只按锚点格找网络,
// 单元自带的杆链只覆盖自己那格),靠这个 part 铺一条主干把它们串起来。
//
// 契约(见 IScenarioPart):Place 只 Submit,忽略传入的 anchor(坐标全在 waypoint 里)。
// 去重:自己维护一个已占格集合,同一格只提交一次 PlaceEntity(重复提交会被拒)。
// 该集合只作跳过判据,不参与决定提交顺序——顺序恒为"按 waypoint 列表、沿线自西向东"。
public sealed class PoleBackbonePart : IScenarioPart
{
    // 相邻杆最大间距(格)。7 是硬连线上限,这里留 1 格余量。
    private const int MaxSpacingTiles = 6;

    private readonly ScenarioProtoIds _ids;
    private readonly IReadOnlyList<(int X, int Y)> _waypoints;

    public PoleBackbonePart(ScenarioProtoIds ids, IReadOnlyList<(int X, int Y)> waypoints)
    {
        _ids = ids;
        _waypoints = waypoints ?? throw new ArgumentNullException(nameof(waypoints));
    }

    public ScenarioPartFacts Place(Simulation sim, int anchorX, int anchorY)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int placed = 0;
        var placedTiles = new HashSet<(int, int)>();

        void Emit(int x, int y)
        {
            if (!placedTiles.Add((x, y))) return;   // 已占格:跳过,绝不重复提交
            sim.Submit(new Command
            {
                Type = CommandType.PlaceEntity,
                ProtoId = _ids.Pole,
                X = x, Y = y, Rotation = 0,
            });
            placed++;
            counts["small-electric-pole"] = counts.TryGetValue("small-electric-pole", out var c) ? c + 1 : 1;
        }

        // 把所有相邻 waypoint 段展开成一条连续的整数点序列
        // (段间与上一段末点重合的接点只保留一个)。
        var path = new List<(int X, int Y)>();
        for (int w = 0; w + 1 < _waypoints.Count; w++)
        {
            var (ax, ay) = _waypoints[w];
            var (bx, by) = _waypoints[w + 1];
            int steps = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
            int startK = path.Count == 0 ? 0 : 1;
            for (int k = startK; k <= steps; k++)
            {
                int x = steps == 0
                    ? ax
                    : ax + (int)Math.Round((double)(bx - ax) * k / steps, MidpointRounding.AwayFromZero);
                int y = steps == 0
                    ? ay
                    : ay + (int)Math.Round((double)(by - ay) * k / steps, MidpointRounding.AwayFromZero);
                path.Add((x, y));
            }
        }
        if (_waypoints.Count == 1) path.Add(_waypoints[0]);

        // 沿点序列铺杆:首点必放,之后每当离上一根 ≥ MaxSpacingTiles 就再放一根。
        // 末点即便不足间距也不强放——上一根离它 < 6,已在连线范围内,链保持连续。
        if (path.Count > 0)
        {
            var last = path[0];
            Emit(last.X, last.Y);
            for (int i = 1; i < path.Count; i++)
            {
                var p = path[i];
                int gap = Math.Max(Math.Abs(p.X - last.X), Math.Abs(p.Y - last.Y));
                if (gap >= MaxSpacingTiles)
                {
                    Emit(p.X, p.Y);
                    last = p;
                }
            }
        }

        return new ScenarioPartFacts(
            EntitiesPlaced: placed,
            EntityCountByType: counts,
            RatedPowerSupplyJPerTick: 0,
            RatedPowerDemandJPerTick: 0,
            MinableOreUnderDrills: 0,
            SeededIronOre: 0,
            FedDrillCount: 0);
    }
}
