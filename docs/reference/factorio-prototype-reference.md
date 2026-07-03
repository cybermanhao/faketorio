# Factorio Prototype 体系参考

> 来源: https://lua-api.factorio.com/latest/index-prototype.html (2026-07 抓取)
> 用途: 为 Faketorio 的数据层(C# Resource prototype 体系)提供领域模型参考。
> 原则: 抄**数据结构与机制字段**,不抄图形/GUI/声音字段(Godot 引擎已提供)。

---

## 1. 最有价值的四个架构级模式

这四个模式比任何单个 prototype 都重要,是 Factorio 数据驱动设计的骨架:

### 1.1 Item ↔ Entity 双向链接
- `ItemPrototype.place_result` → 放置该物品生成哪个实体
- `EntityPrototype.placeable_by` / `minable.result` → 该实体由哪个物品放置、拆除后掉落什么
- 意义: 物品和建筑是两个独立体系,靠 ID 引用连接。蓝图、拆除、快捷栏都建立在这个链接上。

### 1.2 Category 字符串解耦(机器 × 配方 × 资源 × 燃料)
- `CraftingMachine.crafting_categories` ↔ `Recipe.category`
- `MiningDrill.resource_categories` ↔ 资源实体的 category
- `BurnerEnergySource.fuel_categories` ↔ `Item.fuel_category`
- 意义: 机器永远不知道具体配方,只声明"我能做哪类"。加新机器、新配方互不改动。
  对变体游戏尤其重要——换题材=换数据,机制代码零改动。

### 1.3 EnergySource 组件化
实体不硬编码用电还是烧燃料,而是持有一个可替换的 `energy_source` 组件
(Electric / Burner / Heat / Fluid / Void)。
- 意义: 我们 MVP 的"燃料直接发电机"→ M3 换成"锅炉+蒸汽机",机器侧代码不动,
  只换 energy_source 数据。采矿机、机械臂、装配机全部复用同一套能源结算。

### 1.4 Technology effects = 数据化的修改器列表
科技效果是 `Modifier` 数组: `{type="unlock-recipe", recipe="..."}`、速度加成、容量加成等。
- 意义: 科技树完全是数据,不需要为每个科技写代码。

---

## 2. 继承层次(我们要复刻的部分)

```
PrototypeBase (name, type, order, localised_name)
└─ EntityPrototype (collision_box, selection_box, tile_width/height, flags,
   │                minable, placeable_by, fast_replaceable_group)
   └─ EntityWithHealthPrototype (max_health, resistances)   ← 我们暂缓,无战斗
      └─ EntityWithOwnerPrototype
         ├─ TransportBeltConnectablePrototype
         │   ├─ TransportBeltPrototype
         │   ├─ UndergroundBeltPrototype                     ← M2
         │   ├─ SplitterPrototype / LaneSplitterPrototype    ← M2
         │   ├─ LoaderPrototype (Loader1x1 / Loader1x2)      ← 暂缓
         │   └─ LinkedBeltPrototype                          ← 暂缓
         ├─ InserterPrototype
         ├─ CraftingMachinePrototype
         │   ├─ AssemblingMachinePrototype
         │   └─ FurnacePrototype
         ├─ MiningDrillPrototype
         ├─ LabPrototype
         ├─ ContainerPrototype
         │   └─ LogisticContainerPrototype                   ← 阶段 B
         ├─ ElectricPolePrototype
         ├─ GeneratorPrototype / BurnerGeneratorPrototype
         ├─ SolarPanelPrototype / AccumulatorPrototype       ← M2+
         ├─ BoilerPrototype / ReactorPrototype               ← M3+
         └─ RoboportPrototype / FlyingRobotPrototype 系      ← 阶段 B
非实体:
├─ ItemPrototype (及 ToolPrototype=科技包 等派生)
├─ RecipePrototype
└─ TechnologyPrototype
```

---

## 3. 逐项字段参考

### 3.1 EntityPrototype(所有建筑的基类)— M1
| 字段 | 类型 | 含义 |
|---|---|---|
| `collision_box` | BoundingBox | 碰撞盒。**故意比 tile 略小**(如 3×3 机器是 ±1.2),让相邻建筑不误碰。空盒=无碰撞 |
| `selection_box` | BoundingBox | 鼠标选取区域 |
| `tile_width` / `tile_height` | int | 建造时占的格子数,决定放置对齐中心 |
| `build_grid_size` | uint8 | 对齐网格(1×1 或 2×2,铁轨用 2×2) |
| `flags` | flags | "player-creation"、"off-grid" 等行为开关 |
| `minable` | MinableProperties | 拆除耗时 + 掉落物 |
| `placeable_by` | ItemToPlace[] | 由哪个物品放置(蓝图需要) |
| `fast_replaceable_group` | string | 同组建筑可原地快速替换(黄带→红带) |
| `selection_priority` | uint8 | 重叠时的选取优先级 |

### 3.2 TransportBeltPrototype — M1
| 字段 | 含义 |
|---|---|
| `speed` | 每 tick 移动的距离(tile)。**speed × 480 = 物品/秒**(双 lane 合计)。原版黄带 0.03125 → 15/s,红带 0.0625 → 30/s,蓝带 0.09375 → 45/s |
| `related_underground_belt` | 拖动建造时配套的地下带 |
| (继承) `belt_animation_set` | 视觉,不抄,用 Godot 动画 |

**关键机制知识(来自 FFF-176,不在 API 文档里):**
- 传送带每格有 **2 条 lane**,物品占位 0.25 tile,一格每 lane 最多 4 个物品
- 模拟单元是 transport line(相邻带合并),物品存相对 gap,正常流动 O(1)
- 分离器是 line 的天然切分点;有机械臂交互的 line ≤ ~9 tile,无交互可达 100 tile

### 3.3 InserterPrototype — M1
| 字段 | 类型 | 含义 |
|---|---|---|
| `rotation_speed` | double | 每 tick 旋转的圈数(1=一 tick 转一整圈) |
| `extension_speed` | double | 手臂伸缩速度 |
| `pickup_position` / `insert_position` | Vector | 抓取点/放置点(相对实体中心,朝北时);决定臂长——长臂机械臂只是改这两个值 |
| `filter_count` | uint8 | 过滤槽数量(0=不可过滤,最多 5) |
| `energy_source` | EnergySource | 电动或烧燃料 |
| `energy_per_movement` / `energy_per_rotation` | Energy | 按动作计的耗电 |
| `wait_for_full_hand` | bool | 是否攒满一手再放(集装机械臂) |
| `starting_distance`, `bulk` | | 起始臂距 / 是否批量型 |

### 3.4 CraftingMachinePrototype(装配机/熔炉基类)— M1
| 字段 | 含义 |
|---|---|
| `crafting_speed` | 配方耗时倍率,1=标准速 |
| `crafting_categories` | 能处理的配方类别数组(如 "crafting", "smelting") |
| `energy_usage` | 工作时功率(必须为正) |
| `energy_source` | 电/燃料;电源默认 drain = energy_usage/30(待机耗电) |
| `module_slots` / `allowed_effects` | 插件槽(M2+ 再考虑) |
| `fluid_boxes` | 流体接口(M3) |

**AssemblingMachinePrototype 追加:** `fixed_recipe`(固定配方)、`ingredient_count`(可接受配方的最大原料种数)、`source/result_inventory_size`
**FurnacePrototype 追加:** 无需手选配方——根据输入物品自动匹配 smelting 类配方,输入/输出各一格

### 3.5 MiningDrillPrototype — M1
| 字段 | 含义 |
|---|---|
| `mining_speed` | 采矿速度(× 资源的 mining_time = 产出节奏) |
| `resource_searching_radius` | 从中心搜资源的半径(电采 2.49 → 5×5 覆盖,热采 0.99) |
| `resource_categories` | 可采的资源类别 |
| `vector_to_place_result` | 产出物直接放到哪个偏移位置(可直出到传送带上!) |
| `energy_usage` / `energy_source` | 功率/能源 |
| `output_fluid_box` | 抽油机用(M3) |

### 3.6 ElectricPolePrototype — M1
| 字段 | 含义 |
|---|---|
| `supply_area_distance` | 供电半径(3.5 → 7×7 供电区),最大 64 |
| `maximum_wire_distance` | 连线距离上限,最大 64(小杆 7.5,中杆 9,大杆 30,变电站 18) |

覆盖区内的用电实体自动接入该杆所属电网;杆间连线合并成网(连通分量)。

### 3.7 RecipePrototype — M1
| 字段 | 含义 |
|---|---|
| `ingredients` | IngredientPrototype[]: `{type: "item"\|"fluid", name, amount}`,**不允许重复** |
| `results` | ProductPrototype[]: `{type, name, amount, probability?}`,允许重复、允许概率产出 |
| `energy_required` | 标准速度下的耗时秒数(默认 0.5) |
| `category` | 配方类别(默认 "crafting"),决定哪些机器能做 |
| `enabled` | 开局是否可用(false = 需科技解锁) |
| `main_product` | 多产物时 UI 显示哪个为主 |
| `allow_productivity` | 是否吃产能插件(M2+) |

### 3.8 ItemPrototype — M1
| 字段 | 含义 |
|---|---|
| `stack_size` | 单格堆叠上限 |
| `place_result` | 放置后生成的实体 ID |
| `fuel_value` / `fuel_category` | 燃料能量(如 "4MJ")与类别("chemical") |
| `subgroup` / `order` | 背包/配方 UI 的分组排序 |
| `spoil_ticks` / `spoil_result` | 腐坏机制(Space Age,暂不需要,但字段结构可留意) |

### 3.9 TechnologyPrototype — M2
| 字段 | 含义 |
|---|---|
| `unit` | 研究成本: `{count, time, ingredients=[科技包...]}`;每单位消耗一组科技包、耗 time 秒 |
| `prerequisites` | 前置科技 ID 数组 |
| `effects` | Modifier 数组: `unlock-recipe` 为主,还有各类数值加成 |
| `research_trigger` | 替代 unit 的触发式研究(如"采集 X 个某物")——2.0 新手引导用法 |
| `upgrade` / `max_level` | 多级科技(可 "infinite" 无限研究) |

### 3.10 EnergySource 类型 — M1(核心!)

**ElectricEnergySource** — 电网结算的参与方式:
| 字段 | 含义 |
|---|---|
| `buffer_capacity` | 实体内部能量缓冲(蓄电池 "5MJ";普通机器可为极小缓冲) |
| `usage_priority` | 结算优先级,六档: `primary-input` > `secondary-input` > `tertiary`(蓄电池充电) / 供电侧 `primary-output` > `secondary-output`(蓄电池放电) > `solar` 实际结算顺序: solar 先发 → 发电机补 → 蓄电池兜底 |
| `input_flow_limit` / `output_flow_limit` | 进出缓冲的最大功率 |
| `drain` | 待机耗电("最小消耗") |

**BurnerEnergySource** — 燃烧供能:
| 字段 | 含义 |
|---|---|
| `fuel_categories` | 接受的燃料类别(默认 ["chemical"]) |
| `fuel_inventory_size` | 燃料槽数(必填) |
| `effectivity` | 燃烧效率倍率(>0,1=100%) |
| `burnt_inventory_size` | 燃烧残渣槽(核燃料用,默认 0) |

我们的 MVP 发电机 = `BurnerGeneratorPrototype` 思路:Burner 能源输入 + Electric(primary-output)输出。

### 3.11 ResourceEntityPrototype(矿床)— M1
| 字段 | 类型/默认 | 含义 |
|---|---|---|
| `category` | ResourceCategoryID,默认 "basic-solid" | 与采矿机的 `resource_categories` 匹配 |
| `minable` | (继承自 EntityPrototype) | 含 `mining_time` 与产出物——矿床的采集节奏和掉落都在这里 |
| `infinite` | bool,默认 false | 是否无限矿(原版原油);true 时 `minimum`/`normal` 必须非 0 |
| `minimum` / `normal` | uint32 | 无限矿的最低/标准储量基准 |
| `infinite_depletion_amount` | uint32,默认 1 | 无限矿每次开采减少的储量 |
| `randomize_visual_position` | bool,默认 true | 矿石贴图随机偏移,让矿区不那么整齐(表现层参考) |

矿床是**实体**(每 tile 一个,带储量),不是 tile 属性——地图生成时批量落地,采空即移除。

### 3.12 ContainerPrototype(箱子)— M1
| 字段 | 类型/默认 | 含义 |
|---|---|---|
| `inventory_size` | ItemStackIndex,必填 | 格子数(原版木箱 16、铁箱 32、钢箱 48) |
| `inventory_type` | 默认 "with_bar" | "bar" 即限制可用格数的红叉条;M1 可先按 "normal" 实现,bar 后补 |
| `circuit_wire_max_distance` 等 | | 电路网络接口,M3+ 再说 |

### 3.13 BurnerGeneratorPrototype(燃料发电机)— M1
| 字段 | 类型 | 含义 |
|---|---|---|
| `burner` | BurnerEnergySource,必填 | 输入侧:燃料槽、fuel_categories、effectivity(见 3.10) |
| `energy_source` | ElectricEnergySource,必填 | 输出侧:接入电网,usage_priority 应为 primary-output |
| `max_power_output` | Energy,必填 | 最大输出功率,决定烧燃料的速度上限(按需燃烧:电网要多少烧多少,封顶于此) |

原版燃烧室(burner generator)即此类型;我们 MVP 的发电机直接照此建模,M3 换锅炉+蒸汽机时它可继续作为低级发电选项保留。

---

## 4. 明确不参考的部分

- 所有 `*_sprite` / `*_animation` / `*_sound` / `light` / `working_visualisations` 字段 → Godot 场景/动画系统
- `GuiSpec` 及全部 GUI prototype → Godot Control
- `circuit_connector` 系列(电路网络视觉)→ 电路网络本身是 M3+ 甚至更晚,视觉到时用 Godot 做
- `UtilityConstants` 等引擎调参大杂烩 → 需要哪个补哪个

## 5. 起步数值(直接抄原版,后期再调)

| 项目 | 数值 |
|---|---|
| 黄带速度 | 0.03125 tile/tick(15 物品/s) |
| 机械臂(普通) | rotation_speed 0.014, 一次一个物品 |
| 电采矿机 | mining_speed 0.5, 90kW, 搜索半径 2.49 |
| 石炉/钢炉/电炉 crafting_speed | 1 / 2 / 2 |
| 装配机 1/2/3 crafting_speed | 0.5 / 0.75 / 1.25 |
| 小电杆 | 供电 2.5(5×5), 连线 7.5 |
| 煤 fuel_value | 4MJ |
| 铁矿 mining_time | 1(电采矿机 0.5 速 → 每 2s 一矿;产率 = mining_speed / mining_time) |
