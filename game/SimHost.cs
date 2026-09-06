using System.IO;
using Godot;
using Faketorio.Presentation.Core;
using Faketorio.Sim;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Game;

/// autoload 单例。持有唯一 Simulation,是**唯一**调用 Sim.Step() 的地方。
/// 世界的任何改动都必须走 Submit(Command);唯一的例外是 SubmitStartupScene
/// 里那次建造期燃料注入(见下方注释)。
public partial class SimHost : Node
{
    [Export] public long Seed = 20260906L;

    public Simulation Sim { get; private set; } = null!;

    /// 距下一个 sim tick 的分数进度 [0,1)。v1 渲染不插值,留给后续。
    public double Alpha => _acc.Alpha;

    public void Submit(in Command c) => Sim.Submit(c);

    private readonly TickAccumulator _acc = new();

    public override void _Ready()
    {
        Sim = new Simulation(PrototypeLoader.LoadFromDirectory(ResolveDataDir()), Seed);
        SubmitStartupScene();
    }

    public override void _Process(double delta)
    {
        for (int n = _acc.Advance(delta); n-- > 0;) Sim.Step();
    }

    // 编辑器/源码树运行时 res:// 就是 game/,数据在仓库根的 data/base。
    // 导出后 data/ 若被拷进 pck 就落在 res://data/base。两个都试。
    private static string ResolveDataDir()
    {
        string sibling = ProjectSettings.GlobalizePath("res://../data/base");
        if (Directory.Exists(sibling)) return sibling;

        string inside = ProjectSettings.GlobalizePath("res://data/base");
        if (Directory.Exists(inside)) return inside;

        GD.PushError($"Prototype data directory not found (tried '{sibling}' and '{inside}')");
        return sibling;
    }

    /// v1 演示场景:采矿机 -> 传送带 -> 机械臂 -> 木箱,外加发电机 + 电线杆供电。
    /// 全部走 Submit(Command);坐标是照着 data/base/map-gen.json 的 (1,-1) 煤矿
    /// starter patch 挑的,采矿机的 2x2 覆盖区落在煤矿上。
    private void SubmitStartupScene()
    {
        int drill = Sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id;
        int furnace = Sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id;
        int assembler = Sim.Prototypes.Get<AssemblingMachinePrototype>("assembling-machine-1").Id;
        int belt = Sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        int chest = Sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int pole = Sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id;
        int gen = Sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id;
        int inserter = Sim.Prototypes.Get<InserterPrototype>("inserter-basic").Id;

        void Place(int protoId, int x, int y, byte rot = 0)
            => Sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = protoId, X = x, Y = y, Rotation = rot });

        // 一条 5 格向东的传送带 (0,0)..(4,0);出口格是 (4,0)。
        for (int x = 0; x < 5; x++) Place(belt, x, 0, rot: 1);

        // 采矿机 2x2 占 (0,-2)..(1,-1),朝南 -> 输出格 = (0, -2 + 2) = (0,0),正好是传送带入口。
        Place(drill, 0, -2, rot: 2);
        // 机械臂 (5,0) 朝东:身后 (4,0) 是传送带出口格,身前 (6,0) 是木箱。
        Place(inserter, 5, 0, rot: 1);
        Place(chest, 6, 0);

        // 供电:两台 2x2 燃烧发电机 + 两根小电线杆(供电半径 2,连线距离 7)。
        // 杆 (2,-2) 覆盖 x∈[0,4] y∈[-4,0] —— 采矿机 (0,-2) 与两台发电机都在内;
        // 杆 (5,-1) 覆盖 x∈[3,7] y∈[-3,1] —— 机械臂 (5,0) 在内;两杆间距 ~3.2 <= 7,同一网络。
        Place(gen, 1, -4);
        Place(gen, 3, -4);
        Place(pole, 2, -2);
        Place(pole, 5, -1);

        // 纯装饰,给渲染多点花样(空转无所谓)。
        Place(furnace, 8, -2);
        Place(assembler, -5, 2);
        Place(drill, -4, -3);

        Sim.Step();   // drain 放置命令 —— 之后实体确实存在

        // 建造期一次性注入(同 bench ScenarioBuilder 的手法):给两台发电机灌满燃料。
        // 这是本工程唯一不走 Submit 的 sim 写入。
        long coalFuelJ = Sim.Prototypes.Get<ItemPrototype>("coal").FuelValueJ;
        FuelGenerator(1, -4, 50L * coalFuelJ);
        FuelGenerator(3, -4, 50L * coalFuelJ);
    }

    private void FuelGenerator(int x, int y, long joules)
    {
        var id = Sim.World.GetEntityAt(x, y);
        if (id.IsValid) Sim.ElectricGrid.SetFuelBufferJ(id, joules);
    }
}
