namespace Faketorio.Sim.Entities;

public readonly record struct EntityId(int Index, int Generation)
{
    public static readonly EntityId Invalid = new(-1, 0);
    public bool IsValid => Index >= 0;
}
