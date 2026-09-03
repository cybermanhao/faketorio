namespace Faketorio.Sim.Belts;

// tile 坐标 → 覆盖该格的 BeltLineId 的稀疏索引,按 32×32 chunk 组织
// (与 WorldGrid 同套 chunk 键 / 下标算法)。只做点查(合并时判断邻格
// 属于哪条线),从不遍历,所以内部用 Dictionary 不影响确定性。缺省
// 返回 BeltLineId.Invalid。
public sealed class TileToLineIndex
{
    private const int ChunkSize = 32;
    private readonly Dictionary<long, BeltLineId[]> _chunks = new();

    private static long ChunkKey(int tileX, int tileY)
    {
        int cx = tileX >> 5, cy = tileY >> 5;   // 32 = 2^5,向负无穷取整
        return ((long)cx << 32) | (uint)cy;
    }

    private static int TileIndex(int tileX, int tileY)
    {
        int lx = tileX & (ChunkSize - 1), ly = tileY & (ChunkSize - 1);
        return ly * ChunkSize + lx;
    }

    public BeltLineId Get(int x, int y)
        => _chunks.TryGetValue(ChunkKey(x, y), out var c) ? c[TileIndex(x, y)] : BeltLineId.Invalid;

    public void Set(int x, int y, BeltLineId id)
    {
        long key = ChunkKey(x, y);
        if (!_chunks.TryGetValue(key, out var c))
        {
            c = new BeltLineId[ChunkSize * ChunkSize];
            Array.Fill(c, BeltLineId.Invalid);
            _chunks.Add(key, c);
        }
        c[TileIndex(x, y)] = id;
    }

    // 清掉某格的记录(拆除传送带时用)。等价于 Set(x, y, Invalid)。
    public void Clear(int x, int y) => Set(x, y, BeltLineId.Invalid);
}
