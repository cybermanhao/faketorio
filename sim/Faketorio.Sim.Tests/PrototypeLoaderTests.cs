using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class PrototypeLoaderTests
{
    private static PrototypeRegistry Load() => PrototypeLoader.LoadFromDirectory("data/base");

    [Fact]
    public void LoadsItemWithFuelValueInJoules()
    {
        var coal = Load().Get<ItemPrototype>("coal");
        Assert.Equal(4_000_000L, coal.FuelValueJ);
        Assert.Equal("chemical", coal.FuelCategory);
        Assert.Equal(50, coal.StackSize);
    }

    [Fact]
    public void LoadsContainerWithMiningTimeInTicks()
    {
        var chest = Load().Get<ContainerPrototype>("wooden-chest");
        Assert.Equal(16, chest.InventorySize);
        Assert.Equal(30, chest.MiningTimeTicks); // 0.5s * 60
        Assert.Equal(1, chest.TileWidth);
    }

    [Fact]
    public void LoadsRecipeWithTicksAndAmounts()
    {
        var recipe = Load().Get<RecipePrototype>("iron-plate");
        Assert.Equal(192, recipe.EnergyRequiredTicks); // 3.2s * 60
        Assert.Equal("smelting", recipe.Category);
        Assert.Single(recipe.Ingredients);
        Assert.Equal("iron-ore", recipe.Ingredients[0].Name);
    }

    [Fact]
    public void IdsAreStableAndOrderedByName()
    {
        var reg = Load();
        for (int i = 0; i < reg.Count; i++)
            Assert.Equal(i, reg.GetById(i).Id);
        // item 与 entity 同名共存(wooden-chest),各自独立注册
        Assert.NotNull(reg.Get<ItemPrototype>("wooden-chest"));
        Assert.NotNull(reg.Get<ContainerPrototype>("wooden-chest"));
    }

    [Fact]
    public void UnknownTypeThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "[{ \"type\": \"nonsense\", \"name\": \"x\" }]");
        Assert.Throws<InvalidDataException>(() => PrototypeLoader.LoadFromDirectory(dir));
    }

    [Fact]
    public void LoadsTransportBeltWithSpeed()
    {
        var belt = Load().Get<TransportBeltPrototype>("transport-belt-basic");
        Assert.Equal(8, belt.SpeedSubTilesPerTick);
        Assert.Equal(1, belt.TileWidth);
        Assert.Equal(1, belt.TileHeight);
    }

    [Fact]
    public void ZeroTileWidthThrows()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.json"),
            "[{ \"type\": \"container\", \"name\": \"broken-chest\", \"tileWidth\": 0, \"tileHeight\": 1 }]");
        Assert.Throws<InvalidDataException>(() => PrototypeLoader.LoadFromDirectory(dir));
    }

    [Fact]
    public void LoadsResourceWithResolvedNoiseLayer()
    {
        var reg = Load();
        var coal = reg.Get<ResourcePrototype>("coal");
        Assert.Equal("coal", coal.MinableResult);
        Assert.Equal(400, coal.RichnessBase);
        Assert.Equal(44000, coal.Layer.ThresholdQ16);
        Assert.Equal(64, coal.Layer.LatticeSize);     // 从 map-gen 默认补齐
        Assert.Equal(3, coal.Layer.Octaves);          // 从 map-gen 默认补齐
        Assert.Equal(1 + coal.Id, coal.Layer.FieldId); // fieldId 省略 -> 1 + Id
        Assert.False(coal.Layer.Warp);
    }

    [Fact]
    public void LoadsMapGenWithStarterPatches()
    {
        var mg = Load().Get<MapGenPrototype>("default");
        Assert.Equal(5, mg.StarterPatches.Count);
        Assert.Equal(new StarterPatch("coal", 6, -8, 4, 1500), mg.StarterPatches[0]);
    }

    private static void AssertLoadThrows(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gen.json"), json);
        Assert.Throws<InvalidDataException>(() => PrototypeLoader.LoadFromDirectory(dir));
    }

    // 一个最小的自洽数据集:1 item + 1 map-gen + 1 resource。各负例只改坏其中一处。
    private const string ValidItem = "{ \"type\": \"item\", \"name\": \"coal\", \"stackSize\": 50 }";
    private const string ValidResource =
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 400, \"richnessScale\": 6000 }";

    [Fact]
    public void TwoMapGen_Throws() => AssertLoadThrows(
        $"[{ValidItem}, {ValidResource}, " +
        "{ \"type\": \"map-gen\", \"name\": \"a\" }, { \"type\": \"map-gen\", \"name\": \"b\" }]");

    [Fact]
    public void ResourcePresentButNoMapGen_Throws() => AssertLoadThrows(
        $"[{ValidItem}, {ValidResource}]");

    [Fact]
    public void NonPowerOfTwoLatticeSize_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000, \"latticeSize\": 48 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void OctavesTooLargeForLattice_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000, \"latticeSize\": 64, \"octaves\": 8 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void WarpTrue_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000, \"warp\": true }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void ThresholdOutOfRange_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 0 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void RichnessBaseZero_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 0, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void RichnessScaleNegative_Throws() => AssertLoadThrows(
        $"[{ValidItem}, " +
        "{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", " +
        "\"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 400, \"richnessScale\": -1 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void ResourceMinableResultMissingItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"nonexistent\", " +
        "\"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 400, \"richnessScale\": 6000 }, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\" }]");

    [Fact]
    public void StarterPatchUnknownResource_Throws() => AssertLoadThrows(
        $"[{ValidItem}, {ValidResource}, " +
        "{ \"type\": \"map-gen\", \"name\": \"default\", \"starterPatches\": " +
        "[{ \"resource\": \"nonexistent\", \"centerX\": 0, \"centerY\": 0, \"radius\": 3, \"centerAmount\": 100 }] }]");

    [Fact]
    public void StarterPatchWithNoResourcePrototypes_Throws() => AssertLoadThrows(
        "[{ \"type\": \"map-gen\", \"name\": \"default\", \"starterPatches\": " +
        "[{ \"resource\": \"coal\", \"centerX\": 0, \"centerY\": 0, \"radius\": 3, \"centerAmount\": 100 }] }]");

    [Fact]
    public void ResolvesRecipeIngredientAndResultItemIds()
    {
        var reg = Load();
        var gear = reg.Get<RecipePrototype>("iron-gear-wheel");
        int plateId = reg.Get<ItemPrototype>("iron-plate").Id;
        int gearId = reg.Get<ItemPrototype>("iron-gear-wheel").Id;
        Assert.Equal(new[] { new ResolvedAmount(plateId, 2) }, gear.ResolvedIngredients);
        Assert.Equal(new[] { new ResolvedAmount(gearId, 1) }, gear.ResolvedResults);
    }

    [Fact]
    public void LoadsPlayerPrototype()
    {
        var p = Load().Get<PlayerPrototype>("player");
        Assert.Equal(60, p.InventorySize);
        Assert.Equal(1536, p.ReachSubTiles);
        Assert.Equal(2, p.StartingInventory.Count);
        Assert.Equal("iron-plate", p.StartingInventory[0].Name);
    }

    [Fact]
    public void RecipeWithUnknownItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"recipe\", \"name\": \"bad\", \"category\": \"crafting\", " +
        "\"ingredients\": [ { \"name\": \"nonexistent\", \"amount\": 1 } ], " +
        "\"results\": [ { \"name\": \"nonexistent\", \"amount\": 1 } ] }]");

    [Fact]
    public void TwoPlayerPrototypes_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"a\" }, { \"type\": \"player\", \"name\": \"b\" }]");

    [Fact]
    public void PlayerInventorySizeZero_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"player\", \"inventorySize\": 0 }]");

    [Fact]
    public void PlayerStartingInventoryUnknownItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"player\", " +
        "\"startingInventory\": [ { \"name\": \"nonexistent\", \"amount\": 1 } ] }]");

    [Fact]
    public void PlayerReachZero_Throws() => AssertLoadThrows(
        "[{ \"type\": \"player\", \"name\": \"player\", \"reachSubTiles\": 0 }]");

    [Fact]
    public void LoadsElectricPole()
    {
        var pole = Load().Get<ElectricPolePrototype>("small-electric-pole");
        Assert.Equal(7, pole.MaximumWireDistanceTiles);
        Assert.Equal(2, pole.SupplyAreaDistanceTiles);
    }

    [Fact]
    public void LoadsFuelGeneratorWithResolvedFuelId()
    {
        var gen = Load().Get<FuelGeneratorPrototype>("burner-generator");
        int coalId = Load().Get<ItemPrototype>("coal").Id;
        Assert.Equal(1500, gen.PowerOutputJPerTick);   // 90kW / 60 ticks-per-second
        Assert.Equal(coalId, gen.FuelItemProtoId);
    }

    [Fact]
    public void PoleZeroWireDistance_Throws() => AssertLoadThrows(
        "[{ \"type\": \"electric-pole\", \"name\": \"p\", \"maximumWireDistanceTiles\": 0, \"supplyAreaDistanceTiles\": 2 }]");

    [Fact]
    public void PoleZeroSupplyArea_Throws() => AssertLoadThrows(
        "[{ \"type\": \"electric-pole\", \"name\": \"p\", \"maximumWireDistanceTiles\": 7, \"supplyAreaDistanceTiles\": 0 }]");

    [Fact]
    public void GeneratorZeroPowerOutput_Throws() => AssertLoadThrows(
        "[{ \"type\": \"item\", \"name\": \"coal\", \"stackSize\": 50 }, " +
        "{ \"type\": \"fuel-generator\", \"name\": \"g\", \"fuelItemName\": \"coal\" }]");

    [Fact]
    public void GeneratorUnknownFuelItem_Throws() => AssertLoadThrows(
        "[{ \"type\": \"fuel-generator\", \"name\": \"g\", \"powerOutput\": \"90kW\", \"fuelItemName\": \"nonexistent\" }]");

    [Fact]
    public void LoadsFurnace()
    {
        var furnace = Load().Get<FurnacePrototype>("stone-furnace");
        Assert.Equal("smelting", furnace.Category);
        Assert.Equal(1, furnace.InputSlots);
        Assert.Equal(1, furnace.OutputSlots);
        Assert.Equal(1500, furnace.EnergyUsageJPerTick);   // 90kW / 60 ticks-per-second
    }

    [Fact]
    public void LoadsAssemblingMachine()
    {
        var asm = Load().Get<AssemblingMachinePrototype>("assembling-machine-1");
        Assert.Equal("crafting", asm.Category);
        Assert.Equal(2, asm.InputSlots);
        Assert.Equal(1, asm.OutputSlots);
        Assert.Equal(1250, asm.EnergyUsageJPerTick);   // 75kW / 60 ticks-per-second
    }

    [Fact]
    public void CraftingMachineEmptyCategory_Throws() => AssertLoadThrows(
        "[{ \"type\": \"furnace\", \"name\": \"f\", \"category\": \"\", \"inputSlots\": 1, \"outputSlots\": 1 }]");

    [Fact]
    public void CraftingMachineZeroInputSlots_Throws() => AssertLoadThrows(
        "[{ \"type\": \"furnace\", \"name\": \"f\", \"category\": \"smelting\", \"inputSlots\": 0, \"outputSlots\": 1 }]");

    [Fact]
    public void CraftingMachineZeroOutputSlots_Throws() => AssertLoadThrows(
        "[{ \"type\": \"assembling-machine\", \"name\": \"a\", \"category\": \"crafting\", \"inputSlots\": 1, \"outputSlots\": 0 }]");

    [Fact]
    public void CraftingMachineNegativeEnergyUsage_Throws() => AssertLoadThrows(
        "[{ \"type\": \"furnace\", \"name\": \"f\", \"category\": \"smelting\", \"inputSlots\": 1, \"outputSlots\": 1, \"energyUsage\": \"-120W\" }]");
}
