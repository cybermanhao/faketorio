using Faketorio.Sim.Prototypes;
using Faketorio.Sim.State;

namespace Faketorio.Sim.World;

// 惰性无限矿脉层。每个 chunk 首次被查询 / Extract 时生成;chunk 内容是
// (seed, tileX, tileY) 的纯函数,无邻块依赖 -> 访问顺序无关(spec §7)。
public sealed class ResourceGrid
{
    private const int Size = WorldGrid.ChunkSize;               // 32

    private readonly long _seed;
    private readonly ResourcePrototype[] _resources;            // 按 Id 升序,决胜用
    private readonly IReadOnlyList<StarterPatch> _starterPatches;
    private readonly int[] _starterPatchResId;                  // 与 _starterPatches 平行:各斑的矿 proto Id

    private readonly Dictionary<long, ResourceChunk> _chunks = new();
    private readonly List<long> _sortedKeys = new();
    private bool _keysDirty;

    public ResourceGrid(long seed, PrototypeRegistry protos)
    {
        _seed = seed;

        var res = new List<ResourcePrototype>();
        MapGenPrototype? mapGen = null;
        for (int i = 0; i < protos.Count; i++)
        {
            switch (protos.GetById(i))
            {
                case ResourcePrototype r: res.Add(r); break;
                case MapGenPrototype m:   mapGen = m; break;
            }
        }
        res.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        _resources = res.ToArray();

        _starterPatches = mapGen?.StarterPatches ?? Array.Empty<StarterPatch>();
        _starterPatchResId = new int[_starterPatches.Count];
        for (int i = 0; i < _starterPatches.Count; i++)
            _starterPatchResId[i] = protos.Get<ResourcePrototype>(_starterPatches[i].Resource).Id;
    }

    public int GeneratedChunkCount => _chunks.Count;
    public bool IsChunkGenerated(int x, int y) => _chunks.ContainsKey(ChunkKey(x, y));

    public ResourceCell GetResourceAt(int x, int y)
    {
        var chunk = Ensure(x, y);
        int i = TileIndex(x, y);
        return chunk.TypeId[i] == 0 ? ResourceCell.Empty : new ResourceCell(chunk.TypeId[i], chunk.Amount[i]);
    }

    // 无副作用读:已生成 chunk 直接读;未生成则临时 Generate 一个瞬时 chunk 读完丢弃,
    // 不进 _chunks、不置 _keysDirty —— 供表现层在"地图模式"下自由平移查矿而不改状态哈希。
    // Generate 是 (seed, chunk 基点) 纯函数,所以 Peek 结果与之后 GetResourceAt 一致。
    public ResourceCell PeekResourceAt(int x, int y)
    {
        long key = ChunkKey(x, y);
        if (!_chunks.TryGetValue(key, out var chunk))
        {
            chunk = new ResourceChunk();
            Generate(x, y, chunk);
        }
        int i = TileIndex(x, y);
        return chunk.TypeId[i] == 0 ? ResourceCell.Empty : new ResourceCell(chunk.TypeId[i], chunk.Amount[i]);
    }

    public int Extract(int x, int y, int count)
    {
        if (count <= 0) return 0;
        var chunk = Ensure(x, y);
        int i = TileIndex(x, y);
        if (chunk.TypeId[i] == 0) return 0;
        int take = Math.Min(chunk.Amount[i], count);
        chunk.Amount[i] -= take;
        if (chunk.Amount[i] == 0) chunk.TypeId[i] = 0;
        return take;
    }

    public void WriteState(IStateWriter writer)
    {
        if (_keysDirty)
        {
            _sortedKeys.Clear();
            foreach (var k in _chunks.Keys) _sortedKeys.Add(k);
            _sortedKeys.Sort();
            _keysDirty = false;
        }
        for (int s = 0; s < _sortedKeys.Count; s++)
        {
            long key = _sortedKeys[s];
            writer.Write(key);
            var chunk = _chunks[key];
            for (int i = 0; i < chunk.TypeId.Length; i++)
            {
                writer.Write(chunk.TypeId[i]);
                writer.Write(chunk.Amount[i]);
            }
        }
    }

    // ---- chunk math:与 WorldGrid 完全一致(spec Global Constraints) ----
    private static long ChunkKey(int x, int y)
    {
        int cx = x >> 5, cy = y >> 5;
        return ((long)cx << 32) | (uint)cy;
    }

    private static int TileIndex(int x, int y)
        => (y & (Size - 1)) * Size + (x & (Size - 1));

    private ResourceChunk Ensure(int x, int y)
    {
        long key = ChunkKey(x, y);
        if (_chunks.TryGetValue(key, out var chunk))
            return chunk;

        chunk = new ResourceChunk();
        Generate(x, y, chunk);
        _chunks[key] = chunk;
        _keysDirty = true;
        return chunk;
    }

    private void Generate(int anyX, int anyY, ResourceChunk chunk)
    {
        int baseX = (anyX >> 5) << 5;
        int baseY = (anyY >> 5) << 5;

        for (int ly = 0; ly < Size; ly++)
            for (int lx = 0; lx < Size; lx++)
            {
                int wx = baseX + lx, wy = baseY + ly;
                int i = ly * Size + lx;

                // --- 噪声:每种矿独立场,归一化超出量最大者胜,平手取 Id 小 ---
                int bestIdx = -1, bestScore = 0, bestExcess = 0;
                for (int r = 0; r < _resources.Length; r++)
                {
                    var L = _resources[r].Layer;
                    int v = ValueNoise.Fbm(_seed, L.FieldId, wx, wy, L.LatticeSize, L.Octaves);
                    int excess = v - L.ThresholdQ16;
                    if (excess <= 0) continue;
                    int score = (int)(((long)excess << 16) / (65536 - L.ThresholdQ16));
                    if (bestIdx < 0 || score > bestScore)
                    {
                        bestIdx = r; bestScore = score; bestExcess = excess;
                    }
                }
                if (bestIdx >= 0)
                {
                    var rp = _resources[bestIdx];
                    chunk.TypeId[i] = rp.Id;
                    chunk.Amount[i] = rp.RichnessBase + (int)(((long)rp.RichnessScale * bestExcess) >> 16);
                }

                // --- 启动矿斑叠加:列表顺序,靠前的胜,覆盖噪声 ---
                for (int p = 0; p < _starterPatches.Count; p++)
                {
                    var sp = _starterPatches[p];
                    long dx = wx - sp.CenterX, dy = wy - sp.CenterY;
                    int dist = ValueNoise.Isqrt(dx * dx + dy * dy);
                    if (dist >= sp.Radius) continue;
                    int amt = sp.CenterAmount * (sp.Radius - dist) / sp.Radius;
                    if (amt <= 0) continue;
                    chunk.TypeId[i] = _starterPatchResId[p];
                    chunk.Amount[i] = amt;
                    break;
                }
            }
    }
}

// 一个 32x32 chunk 的矿脉数据。每个 tile 有 typeId 和 amount,0 = 空。
internal sealed class ResourceChunk
{
    public readonly int[] TypeId = new int[WorldGrid.ChunkSize * WorldGrid.ChunkSize];   // 0 = empty
    public readonly int[] Amount = new int[WorldGrid.ChunkSize * WorldGrid.ChunkSize];
}
