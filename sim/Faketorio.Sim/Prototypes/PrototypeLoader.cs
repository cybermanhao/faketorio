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
        ResolveAndValidateRecipesAndPlayer(registry);
        ResolveAndValidateElectric(registry);
        ResolveAndValidateCraftingMachines(registry);
        ResolveAndValidateMiningDrills(registry);
        return registry;
    }

    // AssignIds() 之后:校验熔炉/装配机的公共字段。不校验 Category 一定有配方存在——
    // 允许先加机器后加配方的数据组织顺序,运行时匹配不到只是空转,不是加载期错误。
    private static void ResolveAndValidateCraftingMachines(PrototypeRegistry registry)
    {
        for (int i = 0; i < registry.Count; i++)
        {
            if (registry.GetById(i) is not CraftingMachinePrototype m) continue;
            if (string.IsNullOrEmpty(m.Category))
                throw new InvalidDataException($"Crafting machine '{m.Name}': category must be non-empty");
            if (m.InputSlots < 1)
                throw new InvalidDataException($"Crafting machine '{m.Name}': inputSlots must be >= 1");
            if (m.OutputSlots < 1)
                throw new InvalidDataException($"Crafting machine '{m.Name}': outputSlots must be >= 1");
            if (m.EnergyUsageJPerTick < 0)
                throw new InvalidDataException($"Crafting machine '{m.Name}': energyUsage must be >= 0");
        }
    }

    // AssignIds() 之后:校验采矿机字段。
    private static void ResolveAndValidateMiningDrills(PrototypeRegistry registry)
    {
        for (int i = 0; i < registry.Count; i++)
        {
            if (registry.GetById(i) is not MiningDrillPrototype d) continue;
            if (d.EnergyUsageJPerTick < 0)
                throw new InvalidDataException($"Mining drill '{d.Name}': energyUsage must be >= 0");
        }
    }

    // AssignIds() 之后:校验电线杆字段,解析 + 校验发电机的燃料物品名。
    private static void ResolveAndValidateElectric(PrototypeRegistry registry)
    {
        for (int i = 0; i < registry.Count; i++)
        {
            switch (registry.GetById(i))
            {
                case ElectricPolePrototype pole:
                    if (pole.MaximumWireDistanceTiles < 1)
                        throw new InvalidDataException($"Electric pole '{pole.Name}': maximumWireDistanceTiles must be >= 1");
                    if (pole.SupplyAreaDistanceTiles < 1)
                        throw new InvalidDataException($"Electric pole '{pole.Name}': supplyAreaDistanceTiles must be >= 1");
                    break;
                case FuelGeneratorPrototype gen:
                    if (gen.PowerOutputJPerTick < 1)
                        throw new InvalidDataException($"Fuel generator '{gen.Name}': powerOutput must be positive");
                    if (!registry.TryGet<ItemPrototype>(gen.FuelItemName, out var fuelItem))
                        throw new InvalidDataException($"Fuel generator '{gen.Name}': fuelItemName '{gen.FuelItemName}' has no matching item");
                    gen.FuelItemProtoId = fuelItem.Id;
                    break;
            }
        }
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

        // 跨引用校验(starter patch):只要有 map-gen 就跑,与是否有 resource 无关——
        // 否则"有矿斑、无 resource"会加载通过,之后在 ResourceGrid 构造里炸 KeyNotFound。
        if (mapGen is not null)
        {
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
            if (r.RichnessScale < 0 || r.RichnessScale > 1_000_000)
                throw new InvalidDataException($"Resource '{r.Name}': richnessScale must be in 0..1000000");
            if (!registry.TryGet<ItemPrototype>(r.MinableResult, out _))
                throw new InvalidDataException($"Resource '{r.Name}': minableResult '{r.MinableResult}' has no matching item");

            r.Layer = L with { FieldId = fieldId, LatticeSize = lattice, Octaves = octaves };
        }
    }

    // AssignIds() 之后:把每个 RecipePrototype 的 Ingredients/Results 名字解析成
    // proto id,再校验 player prototype。与 ResolveAndValidateMapGen 同风格。
    private static void ResolveAndValidateRecipesAndPlayer(PrototypeRegistry registry)
    {
        int playerCount = 0;
        PlayerPrototype? player = null;
        for (int i = 0; i < registry.Count; i++)
        {
            switch (registry.GetById(i))
            {
                case RecipePrototype r:
                    r.ResolvedIngredients = Resolve(registry, r, r.Ingredients, "ingredient");
                    r.ResolvedResults     = Resolve(registry, r, r.Results, "result");
                    break;
                case PlayerPrototype p:
                    player = p; playerCount++;
                    break;
            }
        }

        if (playerCount > 1)
            throw new InvalidDataException("More than one 'player' prototype");

        if (player is not null)
        {
            if (player.InventorySize < 1)
                throw new InvalidDataException("player: inventorySize must be >= 1");
            if (player.ReachSubTiles < 1)
                throw new InvalidDataException("player: reachSubTiles must be >= 1");
            if (player.WalkSpeedSubTilesPerTick < 1)
                throw new InvalidDataException("player: walkSpeedSubTilesPerTick must be >= 1");
            if (player.CraftQueueCap < 1)
                throw new InvalidDataException("player: craftQueueCap must be >= 1");
            foreach (var ia in player.StartingInventory)
                if (!registry.TryGet<ItemPrototype>(ia.Name, out _))
                    throw new InvalidDataException($"player: startingInventory item '{ia.Name}' has no matching item");
        }
    }

    private static IReadOnlyList<ResolvedAmount> Resolve(
        PrototypeRegistry registry, RecipePrototype r, List<ItemAmount> src, string role)
    {
        var list = new List<ResolvedAmount>(src.Count);
        foreach (var ia in src)
        {
            if (ia.Amount < 1)
                throw new InvalidDataException($"Recipe '{r.Name}': {role} '{ia.Name}' amount must be >= 1");
            if (!registry.TryGet<ItemPrototype>(ia.Name, out var item))
                throw new InvalidDataException($"Recipe '{r.Name}': {role} '{ia.Name}' has no matching item");
            list.Add(new ResolvedAmount(item.Id, ia.Amount));
        }
        return list;
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
                MiningTimeTicks = Units.SecondsToTicks(GetDouble(el, "miningTimeSeconds", 1.0)),
                Layer = ParseNoiseLayer(el),
            },
            "map-gen" => new MapGenPrototype
            {
                Name = name,
                DefaultLatticeSize = GetInt(el, "defaultLatticeSize", 64),
                DefaultOctaves     = GetInt(el, "defaultOctaves", 3),
                StarterPatches     = ParseStarterPatches(el),
            },
            "player" => new PlayerPrototype
            {
                Name = name,
                InventorySize            = GetInt(el, "inventorySize", 60),
                ReachSubTiles            = GetInt(el, "reachSubTiles", 1536),
                WalkSpeedSubTilesPerTick = GetInt(el, "walkSpeedSubTilesPerTick", 38),
                CraftQueueCap            = GetInt(el, "craftQueueCap", 32),
                StartingInventory        = ParseAmounts(el.TryGetProperty("startingInventory", out var si)
                    ? si : default).AsReadOnly(),
            },
            "electric-pole" => ValidateFootprint(new ElectricPolePrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                MaximumWireDistanceTiles = GetInt(el, "maximumWireDistanceTiles", 0),
                SupplyAreaDistanceTiles  = GetInt(el, "supplyAreaDistanceTiles", 0),
            }),
            "fuel-generator" => ValidateFootprint(new FuelGeneratorPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                PowerOutputJPerTick = el.TryGetProperty("powerOutput", out var po) ? Units.ParsePower(po.GetString()!) : 0L,
                FuelItemName = el.GetProperty("fuelItemName").GetString()!,
            }),
            "furnace" => ValidateFootprint(new FurnacePrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                Category = el.GetProperty("category").GetString()!,
                InputSlots = GetInt(el, "inputSlots", 0),
                OutputSlots = GetInt(el, "outputSlots", 0),
                EnergyUsageJPerTick = el.TryGetProperty("energyUsage", out var fEu) ? Units.ParsePower(fEu.GetString()!) : 0L,
            }),
            "assembling-machine" => ValidateFootprint(new AssemblingMachinePrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                Category = el.GetProperty("category").GetString()!,
                InputSlots = GetInt(el, "inputSlots", 0),
                OutputSlots = GetInt(el, "outputSlots", 0),
                EnergyUsageJPerTick = el.TryGetProperty("energyUsage", out var aEu) ? Units.ParsePower(aEu.GetString()!) : 0L,
            }),
            "mining-drill" => ValidateFootprint(new MiningDrillPrototype
            {
                Name = name,
                TileWidth = GetInt(el, "tileWidth", 1),
                TileHeight = GetInt(el, "tileHeight", 1),
                EnergyUsageJPerTick = el.TryGetProperty("energyUsage", out var eu) ? Units.ParsePower(eu.GetString()!) : 0L,
            }),
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
        if (arr.ValueKind == JsonValueKind.Array)
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
