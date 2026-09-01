namespace Faketorio.Sim.Belts;

// 一条完整的逻辑传送带线:组合两条并行 BeltLane,外加"贴到世界上"才需要
// 的方向与覆盖格子。合并只顺同方向、在端点追加一格,所以 Tiles 恒为一段
// 共线直线,LengthSubTiles 恒 = Tiles.Count * TileSubTiles(= 每条 lane 的长度)。
public sealed class BeltLine
{
    // spec 5.6:1 tile = 256 亚格。
    public const int TileSubTiles = 256;

    // 复用 Command.Rotation 约定:0/1/2/3 = 北/东/南/西。构造后不变。
    public readonly byte Direction;

    // 前到后排列,Tiles[0] 是出口所在格,Tiles[^1] 是入口所在格。
    // 列表实例固定,内容在合并时被拼接(Insert(0,..) / Add(..) / 整体替换)。
    public readonly List<(int X, int Y)> Tiles;

    // 单侧合并直接 mutate 这两个实例(ExtendBack/ExtendFront);三路合并
    // 构造一条全新的 BeltLine,带全新的 lane。
    public readonly BeltLane LaneA;
    public readonly BeltLane LaneB;

    public int LengthSubTiles => Tiles.Count * TileSubTiles;

    public BeltLine(byte direction, List<(int X, int Y)> tiles, BeltLane laneA, BeltLane laneB)
    {
        Direction = direction;
        Tiles = tiles;
        LaneA = laneA;
        LaneB = laneB;
    }
}
