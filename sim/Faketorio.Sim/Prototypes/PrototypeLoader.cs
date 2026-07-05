using System.Text.Json;

namespace Faketorio.Sim.Prototypes;

public static class PrototypeLoader
{
    public static PrototypeRegistry LoadFromDirectory(string dir)
    {
        var registry = new PrototypeRegistry();
        var files = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal); // 确定的加载顺序
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var el in doc.RootElement.EnumerateArray())
                registry.Register(Parse(el));
        }
        registry.AssignIds();
        return registry;
    }

    private static PrototypeBase Parse(JsonElement el)
    {
        string type = el.GetProperty("type").GetString()!;
        string name = el.GetProperty("name").GetString()!;
        return type switch
        {
            "item" => new ItemPrototype
            {
                Name = name,
                StackSize = GetInt(el, "stackSize", 50),
                PlaceResult = GetString(el, "placeResult"),
                FuelValueJ = el.TryGetProperty("fuelValue", out var fv) ? Units.ParseEnergy(fv.GetString()!) : 0L,
                FuelCategory = GetString(el, "fuelCategory"),
            },
            "recipe" => new RecipePrototype
            {
                Name = name,
                Category = GetString(el, "category") ?? "crafting",
                EnergyRequiredTicks = Units.SecondsToTicks(GetDouble(el, "energyRequiredSeconds", 0.5)),
                Ingredients = ParseAmounts(el.GetProperty("ingredients")),
                Results = ParseAmounts(el.GetProperty("results")),
                Enabled = !el.TryGetProperty("enabled", out var en) || en.GetBoolean(),
            },
            "container" => new ContainerPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                MinableResult = GetString(el, "minableResult"),
                MiningTimeTicks = Units.SecondsToTicks(GetDouble(el, "miningTimeSeconds", 0)),
                InventorySize = GetInt(el, "inventorySize", 0),
            },
            _ => throw new InvalidDataException($"Unknown prototype type '{type}' (name '{name}')"),
        };
    }

    private static List<ItemAmount> ParseAmounts(JsonElement arr)
    {
        var list = new List<ItemAmount>();
        foreach (var el in arr.EnumerateArray())
            list.Add(new ItemAmount
            {
                Name = el.GetProperty("name").GetString()!,
                Amount = el.GetProperty("amount").GetInt32(),
            });
        return list;
    }

    private static int GetInt(JsonElement el, string prop, int fallback)
        => el.TryGetProperty(prop, out var v) ? v.GetInt32() : fallback;

    private static double GetDouble(JsonElement el, string prop, double fallback)
        => el.TryGetProperty(prop, out var v) ? v.GetDouble() : fallback;

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) ? v.GetString() : null;
}
