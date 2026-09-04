using Faketorio.Sim.Items;
using Faketorio.Sim.State;

namespace Faketorio.Sim;

// 模拟层玩家。不进 EntityPool(不占地)。所有状态变更方法 internal——只有
// Simulation 的"玩家 tick"调。全整数 / int64,无 float。
public sealed class Player
{
    public int X { get; private set; }
    public int Y { get; private set; }
    public byte WalkDir { get; private set; }
    public bool Walking { get; private set; }

    public bool Mining { get; private set; }
    public int MineTargetX { get; private set; }
    public int MineTargetY { get; private set; }
    public long MineProgress { get; private set; }

    private readonly List<CraftJob> _craftQueue = new();
    public IReadOnlyList<CraftJob> CraftQueue => _craftQueue;

    public readonly Inventory Inventory;

    public Player(int inventorySize) => Inventory = new Inventory(inventorySize);

    // 八向单位向量(子格),对角分量取满速(§4.2 近似)。0=北(-Y),顺时针。
    internal static (int dx, int dy) WalkDelta(byte dir, int speed) => dir switch
    {
        0 => (0, -speed),
        1 => (speed, -speed),
        2 => (speed, 0),
        3 => (speed, speed),
        4 => (0, speed),
        5 => (-speed, speed),
        6 => (-speed, 0),
        7 => (-speed, -speed),
        _ => (0, 0),
    };

    internal void SetWalk(byte dir) { Walking = true; WalkDir = dir; }
    internal void StopWalk() => Walking = false;
    internal void MoveTo(int x, int y) { X = x; Y = y; }

    internal void SetMineTarget(int x, int y)
    {
        if (x != MineTargetX || y != MineTargetY) MineProgress = 0;
        MineTargetX = x;
        MineTargetY = y;
        Mining = true;
    }
    internal void StopMining() { Mining = false; MineProgress = 0; }
    internal void TickMineProgress() => MineProgress++;
    internal void ClearMineProgress() => MineProgress = 0;

    internal void EnqueueCraft(int recipeProtoId, int count)
        => _craftQueue.Add(new CraftJob(recipeProtoId, count, 0));
    internal void TickCraftHeadProgress()
        => _craftQueue[0] = _craftQueue[0] with { Progress = _craftQueue[0].Progress + 1 };
    internal void CompleteOneCraftUnit()
    {
        var head = _craftQueue[0];
        if (head.Count <= 1) _craftQueue.RemoveAt(0);
        else _craftQueue[0] = head with { Count = head.Count - 1, Progress = 0 };
    }

    public void WriteState(IStateWriter writer)
    {
        writer.Write(X);
        writer.Write(Y);
        writer.Write(WalkDir);
        writer.Write((byte)(Walking ? 1 : 0));
        writer.Write((byte)(Mining ? 1 : 0));
        writer.Write(MineTargetX);
        writer.Write(MineTargetY);
        writer.Write(MineProgress);
        writer.Write(_craftQueue.Count);
        for (int i = 0; i < _craftQueue.Count; i++)
        {
            writer.Write(_craftQueue[i].RecipeProtoId);
            writer.Write(_craftQueue[i].Count);
            writer.Write(_craftQueue[i].Progress);
        }
        Inventory.WriteState(writer);
    }
}
