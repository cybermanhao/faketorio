namespace Faketorio.Sim.Commands;

public enum CommandType : byte
{
    PlaceEntity = 1,
    RemoveEntity = 2,
    MovePlayer = 3,
    StopPlayer = 4,
    MineStart = 5,
    MineStop = 6,
    CraftEnqueue = 7,
    TransferToEntity = 8,
    TransferFromEntity = 9,
    SetRecipe = 10,
    RotateEntity = 11,
    BuildFromInventory = 12,
    MoveInventorySlot = 13,   // X = 源槽下标, Y = 目标槽下标(都是 Player.Inventory 的槽位);
                               // 目标空 = 移动,同类 = 按 stackSize 合并(溢出留在源槽),
                               // 异类 = 整体交换。玩家背包内部拖拽/点选操作的唯一入口。
}

public struct Command
{
    public CommandType Type;
    public int ProtoId;
    public int X;
    public int Y;
    public byte Rotation; // 0/1/2/3 = 北/东/南/西
    public int Count;
}
