namespace Faketorio.Sim.Belts;

// 一条传送带 lane 上的物品流,采用 FFF-176 的 gap 表示法(spec 5.2/5.6):
// 不存储每个物品的绝对坐标,只记录物品之间(以及最前物品到出口)的相对
// 距离——整数亚格单位,1 tile = 256。这让正常流动只需增减最前面一个 gap
// (O(1));堵塞时也只需增减"最后一个未压缩到 0 的 gap"。
//
// lineLengthSubTiles 是这条 lane 可用的总长度。M1 Plan 2 只在单个传送带
// 格子(256)上使用这个类型;之后若要把相邻传送带合并成更长的 line,只
// 需传入更大的长度——本类型的算法不需要改动。
public sealed class BeltLane
{
    // spec: 物品间距 0.25 tile = 64 亚格单位(每个物品占用的"槽宽")。
    public const int ItemWidthSubTiles = 64;

    // 从前(出口,position 0)到后(入口)排列。
    // _gaps[0] = 出口到最前物品前沿的距离;
    // _gaps[i](i>0) = 物品 i-1 后沿到物品 i 前沿的距离。
    private readonly List<int> _gaps = new();
    private readonly int _lineLengthSubTiles;

    public BeltLane(int lineLengthSubTiles)
    {
        if (lineLengthSubTiles < ItemWidthSubTiles)
            throw new ArgumentOutOfRangeException(nameof(lineLengthSubTiles));
        _lineLengthSubTiles = lineLengthSubTiles;
    }

    public int Count => _gaps.Count;

    // 仅供测试内省:前到后的 gap 列表,_gaps[0] = 出口到最前物品的距离。
    public IReadOnlyList<int> Gaps => _gaps;

    // 队尾(入口侧)剩余的空闲亚格数——最后一个物品之后到 line 尾端的空间。
    private int BackFreeSubTiles()
    {
        int used = 0;
        foreach (var g in _gaps) used += g;
        used += _gaps.Count * ItemWidthSubTiles;
        return _lineLengthSubTiles - used;
    }

    // 在队尾(入口)插入一个新物品,新物品贴着 line 的入口边界进入。
    // 空间不足时返回 false,不改变任何状态。
    public bool TryInsertAtBack()
    {
        int free = BackFreeSubTiles();
        if (free < ItemWidthSubTiles) return false;
        _gaps.Add(free - ItemWidthSubTiles);
        return true;
    }
}
