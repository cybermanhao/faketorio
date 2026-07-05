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
}
