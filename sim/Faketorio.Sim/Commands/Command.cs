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
