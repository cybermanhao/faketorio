using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Bench.Scenario;

// 场景搭建期用到的全部 prototype id + 数值,一次性解析。基准场景要放几万个
// 实体,每个都走 Prototypes.Get<T>(string) 的字典查找是纯浪费;更重要的是
// 把"名字"集中在一处,几何代码里只出现 int。
public sealed class ScenarioProtoIds
{
    public int Drill { get; }
    public int Furnace { get; }
    public int Belt { get; }
    public int Inserter { get; }
    public int Pole { get; }
    public int Generator { get; }
    public int WoodenChest { get; }
    public int LargeChest { get; }
    public int IronOre { get; }
    public int IronPlate { get; }
    public int Coal { get; }

    public int IronOreStackSize { get; }

    public long DrillEnergyJPerTick { get; }
    public long FurnaceEnergyJPerTick { get; }
    public long InserterEnergyJPerTick { get; }
    public long GeneratorOutputJPerTick { get; }
    public long CoalFuelValueJ { get; }

    // 电线杆的覆盖/连线参数——几何代码要靠它们摆杆子,别在别处再查一遍。
    public int PoleSupplyAreaDistanceTiles { get; }
    public int PoleMaximumWireDistanceTiles { get; }

    public ScenarioProtoIds(PrototypeRegistry protos)
    {
        var drill = protos.Get<MiningDrillPrototype>("electric-mining-drill");
        var furnace = protos.Get<FurnacePrototype>("stone-furnace");
        var belt = protos.Get<TransportBeltPrototype>("transport-belt-basic");
        var inserter = protos.Get<InserterPrototype>("inserter-basic");
        var pole = protos.Get<ElectricPolePrototype>("small-electric-pole");
        var generator = protos.Get<FuelGeneratorPrototype>("burner-generator");
        var woodenChest = protos.Get<ContainerPrototype>("wooden-chest");
        var largeChest = protos.Get<ContainerPrototype>("large-chest");
        var ironOre = protos.Get<ItemPrototype>("iron-ore");
        var ironPlate = protos.Get<ItemPrototype>("iron-plate");
        var coal = protos.Get<ItemPrototype>("coal");

        Drill = drill.Id;
        Furnace = furnace.Id;
        Belt = belt.Id;
        Inserter = inserter.Id;
        Pole = pole.Id;
        Generator = generator.Id;
        WoodenChest = woodenChest.Id;
        LargeChest = largeChest.Id;
        IronOre = ironOre.Id;
        IronPlate = ironPlate.Id;
        Coal = coal.Id;

        IronOreStackSize = ironOre.StackSize;

        DrillEnergyJPerTick = drill.EnergyUsageJPerTick;
        FurnaceEnergyJPerTick = furnace.EnergyUsageJPerTick;
        InserterEnergyJPerTick = inserter.EnergyUsageJPerTick;
        GeneratorOutputJPerTick = generator.PowerOutputJPerTick;
        CoalFuelValueJ = coal.FuelValueJ;

        PoleSupplyAreaDistanceTiles = pole.SupplyAreaDistanceTiles;
        PoleMaximumWireDistanceTiles = pole.MaximumWireDistanceTiles;
    }
}
