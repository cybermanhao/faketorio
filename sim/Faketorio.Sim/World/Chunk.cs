using Faketorio.Sim.Entities;

namespace Faketorio.Sim.World;

public sealed class Chunk
{
    public readonly EntityId[] Tiles; // 行主序 32×32

    public Chunk()
    {
        Tiles = new EntityId[WorldGrid.ChunkSize * WorldGrid.ChunkSize];
        Array.Fill(Tiles, EntityId.Invalid);
    }
}
