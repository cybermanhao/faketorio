namespace Faketorio.Sim.Prototypes;

public abstract class PrototypeBase
{
    public required string Name { get; init; }
    public int Id { get; internal set; } = -1;
}
