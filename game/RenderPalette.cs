using Godot;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Game;

/// prototype 运行时类型 -> 占位颜色。v1 手填,无 sprite。
public static class RenderPalette
{
    public static Color ForEntity(PrototypeBase proto) => proto switch
    {
        MiningDrillPrototype => new Color("#d98e5b"),
        FurnacePrototype => new Color("#c0523a"),
        AssemblingMachinePrototype => new Color("#5b8ad9"),
        CraftingMachinePrototype => new Color("#5b8ad9"),   // 未来新的加工机类型的兜底
        TransportBeltPrototype => new Color("#4a5568"),   // 去饱和蓝灰,读作"传送带"
        InserterPrototype => new Color("#d9c85b"),
        ElectricPolePrototype => new Color("#8a6d3b"),
        FuelGeneratorPrototype => new Color("#6b4a2a"),
        ContainerPrototype => new Color("#9a7b3b"),
        _ => new Color("#888888"),
    };

    public static Color ForResource(int resourceProtoId, PrototypeBase proto) => proto is ResourcePrototype rp
        ? rp.Name switch
        {
            "iron-ore" => new Color(0.55f, 0.60f, 0.70f, 0.5f),
            "copper-ore" => new Color(0.80f, 0.50f, 0.30f, 0.5f),
            "coal" => new Color(0.15f, 0.15f, 0.15f, 0.6f),
            "stone" => new Color(0.60f, 0.55f, 0.45f, 0.5f),
            _ => new Color(0.5f, 0.5f, 0.5f, 0.4f),
        }
        : new Color(0.5f, 0.5f, 0.5f, 0.4f);

    public static Color ForItem(int itemProtoId) => new Color("#f0f0f0");   // 近白,带下方深色描边,在带上滑动很显眼
}
