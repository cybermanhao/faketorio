# Faketorio 设计文档

日期: 2026-07-03
状态: 已与用户确认的设计基线
配套参考: [`docs/reference/factorio-prototype-reference.md`](../../reference/factorio-prototype-reference.md)

## 1. 项目定位

以 Factorio 为蓝本的自动化工厂游戏**变体**:机制相近、题材/美术自定(题材待定,架构上通过数据驱动保证"换题材=换数据")。

硬性要求:**从第一天按大实体量(十万级)设计性能**,参照 Factorio 官方博客(FFF)公开的优化方案,而不是先做朴素版再重写。

不做(明确出范围):战斗/虫族系统、多人联机(但保留确定性模拟,不堵死未来)、网页版导出。

## 2. 技术栈

- Godot 4.5.1 Mono(已安装于 `C:\Program Files\Godot_v4.5.1-stable_mono_win64\`)
- 全 C#(.NET):模拟层、游戏逻辑、UI 胶水统一用 C#,不搞 GDScript 混合
- 选择理由:解释执行的 GDScript 无法满足每秒百万级内循环;且 GDScript 没有值类型 struct,无法表达 FFF-176 所需的连续内存布局(gap 数组、SoA 实体池);C# 具备真多线程与成熟 profiler

## 3. 总体架构:三层严格分离

```
数据层 (Prototypes)   纯 C# POCO,JSON 数据文件 → PrototypeRegistry
模拟层 (Simulation)   纯 C#,零 Godot 节点,固定 60 UPS 确定性更新
表现层 (Presentation) Godot 节点,只读模拟状态渲染;玩家操作以命令提交
```

铁律:
1. 模拟层与数据层均**不引用任何 Godot 类型**,可在纯 .NET 进程中无头运行(xUnit 单测/基准测试)。
2. 表现层单向读取模拟层;一切世界变更(放置/拆除/旋转/配置)走**命令队列**进入模拟层。命令的 tick 边界语义:命令在**下一 tick 开始时按提交顺序统一应用**,tick 中途不插入——确定性回放依赖此定义。这同时是阶段 B 蓝图系统的前置(蓝图=命令的数据化描述)。
3. 确定性:固定 tick;关键路径数值表示见 5.6 节;禁止依赖迭代顺序不定的容器;同种子+同命令序列必须产生相同状态。
4. 模拟层全部状态**可规范序列化**:这是确定性状态哈希(M1)与存/读档(M2)共用的地基,任何新增状态必须纳入序列化范围。

## 4. 数据层:Prototype 体系

复刻 Factorio prototype 体系(详见配套参考文档),**纯 C# POCO 类**(不继承 Godot Resource):
`ItemPrototype`、`RecipePrototype`、`TechnologyPrototype`、`EntityPrototype`(派生 MiningDrill/TransportBelt/Inserter/CraftingMachine/Container/ElectricPole/Generator…)。

数据文件用 **JSON**,由引擎无关的加载器读入 `PrototypeRegistry`。选 POCO+JSON 而非 Godot Resource(.tres)的原因:① Godot C# 的 Resource 派生类离开引擎运行时无法实例化,会使 xUnit 无头测试(铁律 1)不成立;② 数据可脱离编辑器批量生成/校验,服务"换题材=换数据";③ Factorio 自身的数据也是引擎外置(Lua)。代价是放弃 Inspector 可视化编辑,可接受。

四个必须保真的架构模式:
1. **Item ↔ Entity 双向链接**(`place_result` / `placeable_by` / `minable.result`)
2. **Category 字符串解耦**:`crafting_categories`↔`recipe.category`、`resource_categories`、`fuel_categories`——机器与配方永不直接耦合
3. **EnergySource 组件化**:实体持有可替换能源组件(Electric/Burner/Void),电↔燃料切换不改机器代码
4. **Technology effects = Modifier 数据数组**(`unlock-recipe` 等),科技树零专用代码

起步数值直接抄原版(见参考文档第 5 节)。

## 5. 模拟层

### 5.1 世界与实体存储
- tile 网格世界,按 32×32 chunk 组织;地图生成含矿脉分布
- 实体按类型分池,SoA(struct-of-arrays)布局的 C# struct 数组;实体 ID = 池索引 + 代数(generation),不用对象海
- tile → 实体 ID 的空间索引,支持多格建筑(footprint 占位)

### 5.2 传送带(FFF-176 方案)
- 模拟单元是 **transport line**(相邻带合并),不是单格带子;分离器为天然切分点;与机械臂交互的线段 ≤ ~9 tile,无交互可到 100 tile;动态合并/拆分
- 每格 2 条 lane,物品占 0.25 tile;物品存**相对 gap(整数)**,不存绝对坐标
- 正常流动:每 tick 只增减线段头尾两个整数,O(1)
- 堵塞:缓存最后一个非零 gap 的索引(只减不增),摊还 O(1)
- 机械臂对带上物品做增量追踪定位

### 5.3 实体休眠(active list)
全局只 tick"活跃实体列表"。无事可做的实体(缺料的机器、输出满、无电、空转的机械臂)移出列表,由事件唤醒(库存变更、物品抵达、电力恢复、新建筑接入)。加工状态机沿用旧项目 PlacedMachine 验证过的流程:配方匹配 → 能源检查 → 进度推进 → 完成时原子放置输出(全有或全无预检),输出堵塞进 pending 缓冲,完成前重校验输入。

### 5.4 电力网络
- 电线杆按 `maximum_wire_distance` 连线,连通分量 = 一个电网;`supply_area_distance` 覆盖区内的用电实体挂到该网
- 每 tick 每个电网**汇总结算一次**(机器不各自算电):按 `usage_priority` 六档结算——solar 先发 → 发电机(primary/secondary-output)补 → 蓄电池兜底;需求侧 primary-input > secondary-input > tertiary(蓄电池充电)
- 供不应求时按比例降速(satisfaction 系数广播给用电实体)
- MVP 发电:燃料直接发电机(Burner 入 + Electric primary-output 出),流体后置到 M3

### 5.5 性能验收与零分配纪律
- 基准测试是各里程碑验收标准的一部分:脚本生成的大型场景(万级传送带+物品起步,逐里程碑加码)输出 UPS 报告,进 CI 防回退。
- **稳态 tick 零分配**:模拟 tick 在稳定运行时不产生任何托管堆分配——事件、唤醒列表、命令缓冲全部池化/预分配,热路径禁用 LINQ、闭包与装箱。GC 尖峰与 SoA 布局同级,属于"事后补救成本极高"的地基项;基准测试同时输出分配量,非零即失败。

### 5.6 数值表示约定(确定性的基石)
| 量 | 表示 | 说明 |
|---|---|---|
| 能量 | int64,单位焦耳 | 存量、缓冲、fuel_value 全用整数焦耳 |
| 功率 | int64,单位焦耳/tick | 90kW = 1500 J/tick,原版数值在 60 UPS 下整除 |
| 传送带位置/间距 | int32 亚格单位,1 tile = 256 | 物品间距 0.25 tile = 64;gap 数组即整数数组 |
| 加工/研究进度 | int64 定点累加 | 每 tick 加 crafting_speed×基准值的定点结果,达到 energy_required 换算的阈值即完成 |
| 电网 satisfaction | Q16.16 定点系数 | 结算后广播给用电实体,乘法后移位,不引入 float |
| 实体坐标 | tile 用 int32,格内偏移用亚格单位 | 模拟层无 float 坐标;float 只存在于表现层插值 |

模拟层禁止 `float`/`double` 参与任何影响状态的计算;违反即确定性哈希测试失败。

## 6. 表现层

- 地形:TileMapLayer;实体:只渲染相机视野内 chunk,同类实体 MultiMesh 批量绘制;带上物品按 transport line 的 gap 数据反算屏幕位置
- 60 UPS 模拟与渲染帧率解耦,视觉插值
- UI:Godot Control(背包、机器面板、快捷栏、光标堆叠)。交互规格参考旧项目 `godot-playgroud/docs` 的 inventory/cursor-slot/machine-panel 设计文档

## 7. 路线图

**阶段 A:机器人前(传送带物流时代)——主要玩法**

- **M1(MVP,垂直切片)**:地图生成(矿脉)、玩家移动、**玩家背包 + 手挖 + 手搓(简单合成菜单)+ 开局物资包**、建造命令(放置/旋转/拆除)、电力采矿机、传送带、机械臂、熔炉、装配机、箱子、燃料发电机 + 电线杆 + 电网结算(含缺电降速)、确定性状态哈希基建。闭环:挖矿→冶炼→组装→存储,全程电力驱动。附基准测试场景。
  - 自举路径(解决"发电机要煤→煤要采矿机→采矿机要电"的死锁):开局物资包给首批建材,手挖供第一台发电机的启动煤,手搓补建材缺口。
- **M2**:分离器、地下传送带、科技树 + 实验室 + 科技包、更多中间产物配方、**存/读档**(基于铁律 4 的规范序列化)。
- **M3**:流体系统(真管网),锅炉+蒸汽机,依赖流体的产线。

**阶段 B:机器人后(物流网络时代)**

roboport、物流箱体系、建设/物流机器人、**蓝图系统**、ghost(幽灵实体)。前置已埋在 M1:命令化建造 + 任何建筑可由数据描述。

## 8. 测试策略

- 模拟层无头单测(xUnit):传送带压缩/堵塞/合并行为、配方加工物质守恒、电网结算优先级、机械臂增量追踪
- **确定性测试**:同种子+同命令序列跑两遍,全状态哈希一致
- 基准测试防性能回退(见 5.5)

## 9. 关键风险与对策

| 风险 | 对策 |
|---|---|
| FFF-176 传送带实现复杂度高 | M1 最先做、配最全的单测;博文只给思路,细节(拆分/合并、机械臂交互)靠测试驱动逐步逼近 |
| C# ↔ Godot 边界(渲染读模拟状态)成为帧率瓶颈 | 表现层只读视野内数据;必要时模拟层为渲染准备紧凑快照缓冲 |
| 过度设计(为十万实体优化拖慢 M1) | 性能"地基"(SoA/transport line/休眠/命令化)是 M1 范围;其余优化(多线程、C++ 热点)按基准测试数据再上 |
