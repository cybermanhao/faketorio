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
        ResolveAndValidateMapGen(registry);
        return registry;
    }

    // spec §4.1 / §4.3:AssignIds() 之后跑一次。把每个 ResourcePrototype.Layer 的
    // 0 哨兵补成具体值(field = explicit 或 1+Id;格距/倍频 = explicit 或 map-gen 默认),
    // 再校验解析后的有效值。跨引用校验(starter patch / minableResult)也在这里。
    private static void ResolveAndValidateMapGen(PrototypeRegistry registry)
    {
        var resources = new List<ResourcePrototype>();
        MapGenPrototype? mapGen = null;
        int mapGenCount = 0;
        for (int i = 0; i < registry.Count; i++)
        {
            switch (registry.GetById(i))
            {
                case ResourcePrototype r: resources.Add(r); break;
                case MapGenPrototype m:   mapGen = m; mapGenCount++; break;
            }
        }

        if (mapGenCount > 1)
            throw new InvalidDataException("More than one 'map-gen' prototype");
        if (resources.Count == 0)
            return;                       // 没有矿:map-gen 可有可无,无需解析
        if (mapGen is null)
            throw new InvalidDataException("'resource' prototypes present but no 'map-gen' prototype");

        foreach (var r in resources)
        {
            var L = r.Layer;
            int fieldId = L.FieldId != 0 ? L.FieldId : 1 + r.Id;
            int lattice = L.LatticeSize != 0 ? L.LatticeSize : mapGen.DefaultLatticeSize;
            int octaves = L.Octaves != 0 ? L.Octaves : mapGen.DefaultOctaves;

            if (lattice <= 0 || (lattice & (lattice - 1)) != 0)
                throw new InvalidDataException($"Resource '{r.Name}': latticeSize {lattice} is not a positive power of two");
            int log2 = System.Numerics.BitOperations.Log2((uint)lattice);
            if (octaves < 1 || octaves > log2 + 1)
                throw new InvalidDataException($"Resource '{r.Name}': octaves {octaves} out of range 1..{log2 + 1} for latticeSize {lattice}");
            if (L.Warp || L.Ridge)
                throw new InvalidDataException($"Resource '{r.Name}': noise warp/ridge not supported in M1");
            if (L.ThresholdQ16 < 1 || L.ThresholdQ16 > 65535)
                throw new InvalidDataException($"Resource '{r.Name}': thresholdQ16 {L.ThresholdQ16} out of range 1..65535");
            if (r.RichnessBase < 1)
                throw new InvalidDataException($"Resource '{r.Name}': richnessBase must be >= 1");
            if (!registry.TryGet<ItemPrototype>(r.MinableResult, out _))
                throw new InvalidDataException($"Resource '{r.Name}': minableResult '{r.MinableResult}' has no matching item");

            r.Layer = L with { FieldId = fieldId, LatticeSize = lattice, Octaves = octaves };
        }

        foreach (var sp in mapGen.StarterPatches)
        {
            if (!registry.TryGet<ResourcePrototype>(sp.Resource, out _))
                throw new InvalidDataException($"Starter patch references unknown resource '{sp.Resource}'");
            if (sp.Radius < 1)
                throw new InvalidDataException($"Starter patch for '{sp.Resource}': radius must be >= 1");
            if (sp.CenterAmount < 1)
                throw new InvalidDataException($"Starter patch for '{sp.Resource}': centerAmount must be >= 1");
        }
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
            "container" => ValidateFootprint(new ContainerPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                MinableResult = GetString(el, "minableResult"),
                MiningTimeTicks = Units.SecondsToTicks(GetDouble(el, "miningTimeSeconds", 0)),
                InventorySize = GetInt(el, "inventorySize", 0),
            }),
            "transport-belt" => ValidateFootprint(new TransportBeltPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                SpeedSubTilesPerTick = GetInt(el, "speedSubTilesPerTick", 0),
            }),
            "resource" => new ResourcePrototype
            {
                Name = name,
                MinableResult = el.GetProperty("minableResult").GetString()!,
                RichnessBase  = GetInt(el, "richnessBase", 0),
                RichnessScale = GetInt(el, "richnessScale", 0),
                Layer = ParseNoiseLayer(el),
            },
            "map-gen" => new MapGenPrototype
            {
                Name = name,
                DefaultLatticeSize = GetInt(el, "defaultLatticeSize", 64),
                DefaultOctaves     = GetInt(el, "defaultOctaves", 3),
                StarterPatches     = ParseStarterPatches(el),
            },
            _ => throw new InvalidDataException($"Unknown prototype type '{type}' (name '{name}')"),
        };
    }

    // 缺省字段存 0(哨兵),由 ResolveAndValidateMapGen 补齐。
    private static NoiseLayer ParseNoiseLayer(JsonElement el)
    {
        if (!el.TryGetProperty("noise", out var n))
            return new NoiseLayer(0, 0, 0, 0);
        return new NoiseLayer(
            FieldId:      GetInt(n, "fieldId", 0),
            LatticeSize:  GetInt(n, "latticeSize", 0),
            Octaves:      GetInt(n, "octaves", 0),
            ThresholdQ16: GetInt(n, "thresholdQ16", 0),
            Warp:  n.TryGetProperty("warp",  out var w) && w.GetBoolean(),
            Ridge: n.TryGetProperty("ridge", out var r) && r.GetBoolean());
    }

    private static IReadOnlyList<StarterPatch> ParseStarterPatches(JsonElement el)
    {
        var list = new List<StarterPatch>();
        if (el.TryGetProperty("starterPatches", out var arr))
            foreach (var p in arr.EnumerateArray())
                list.Add(new StarterPatch(
                    p.GetProperty("resource").GetString()!,
                    p.GetProperty("centerX").GetInt32(),
                    p.GetProperty("centerY").GetInt32(),
                    p.GetProperty("radius").GetInt32(),
                    p.GetProperty("centerAmount").GetInt32()));
        return list;
    }

    // 占地为 0(或负)的实体会通过 IsAreaFree/OccupyArea 的空循环"合法"放置,
    // 却不占任何 tile,导致 RemoveEntity 永远找不到它——数据加载期直接拒绝。
    private static T ValidateFootprint<T>(T proto) where T : EntityPrototype
    {
        if (proto.TileWidth <= 0 || proto.TileHeight <= 0)
            throw new InvalidDataException(
                $"Entity prototype '{proto.Name}' has non-positive footprint " +
                $"(tileWidth={proto.TileWidth}, tileHeight={proto.TileHeight})");
        return proto;
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
