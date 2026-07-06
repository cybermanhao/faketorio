using Faketorio.Sim.Entities;

namespace Faketorio.Sim.World;

public sealed class WorldGrid
{
    public const int ChunkSize = 32;

    private readonly Dictionary<long, Chunk> _chunks = new();
    private readonly List<long> _sortedKeys = new();
    private bool _keysDirty;

    private static long ChunkKey(int tileX, int tileY)
    {
        // 向负无穷取整的 chunk 坐标
        int cx = tileX >> 5, cy = tileY >> 5; // 32 = 2^5
        return ((long)cx << 32) | (uint)cy;
    }

    private static int TileIndex(int tileX, int tileY)
    {
        int lx = tileX & (ChunkSize - 1), ly = tileY & (ChunkSize - 1);
        return ly * ChunkSize + lx;
    }

    public EntityId GetEntityAt(int x, int y)
        => _chunks.TryGetValue(ChunkKey(x, y), out var c) ? c.Tiles[TileIndex(x, y)] : EntityId.Invalid;

    public bool IsAreaFree(int x, int y, int w, int h)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                if (GetEntityAt(x + dx, y + dy).IsValid)
                    return false;
        return true;
    }

    public void OccupyArea(int x, int y, int w, int h, EntityId id)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                GetOrCreateChunk(x + dx, y + dy).Tiles[TileIndex(x + dx, y + dy)] = id;
    }

    public void ClearArea(int x, int y, int w, int h)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                if (_chunks.TryGetValue(ChunkKey(x + dx, y + dy), out var c))
                    c.Tiles[TileIndex(x + dx, y + dy)] = EntityId.Invalid;
    }

    public IReadOnlyList<long> SortedChunkKeys()
    {
        if (_keysDirty)
        {
            _sortedKeys.Clear();
            foreach (var k in _chunks.Keys) _sortedKeys.Add(k);
            _sortedKeys.Sort();
            _keysDirty = false;
        }
        return _sortedKeys;
    }

    public Chunk GetChunkByKey(long key) => _chunks[key];

    private Chunk GetOrCreateChunk(int tileX, int tileY)
    {
        long key = ChunkKey(tileX, tileY);
        if (!_chunks.TryGetValue(key, out var chunk))
        {
            chunk = new Chunk();
            _chunks.Add(key, chunk);
            _keysDirty = true;
        }
        return chunk;
    }
}
