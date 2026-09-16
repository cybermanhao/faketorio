namespace Faketorio.Sim.Prototypes;

public abstract class EntityPrototype : PrototypeBase
{
    public int TileWidth { get; init; } = 1;
    public int TileHeight { get; init; } = 1;
    public string? MinableResult { get; init; }
    public int MiningTimeTicks { get; init; }

    // 玩家碰撞盒 = 完整占地矩形四边各向内缩这么多子格(1 tile = 256)。
    // 0 = 碰撞盒 == 占地(默认,历史行为)。大到某轴 min >= max 时碰撞盒为空,
    // 该实体对玩家完全可通行。只影响玩家移动;放置/占用仍用 TileWidth × TileHeight。
    public int CollisionInsetSubTiles { get; init; }
}

public sealed class ContainerPrototype : EntityPrototype
{
    public int InventorySize { get; init; }
}
