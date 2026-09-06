# Faketorio 模拟层 Benchmark

固定种子中型工厂,跑固定 tick,校验世界状态哈希 golden 值 + 记录 UPS/分阶段耗时。

## 跑

    dotnet run -c Release --project sim/Faketorio.Sim.Bench

默认 `scale=50 ticks=5000`(warmup 1 趟 + 3 趟干净计时 + 1 趟带 profiler 的分阶段趟)。

## 参数

| 参数 | 默认 | 含义 |
|------|------|------|
| `--scale N` | 50 | 单元方阵规模(采矿机单元数) |
| `--ticks N` | 5000 | 每趟模拟 tick 数 |
| `--warmup N` | 1 | 计时前的热身趟数 |
| `--iterations N` | 3 | 干净计时趟数(min/median 取自这些) |
| `--golden <path>` | `bench/golden.json` | golden 文件路径 |
| `--report <path>` | `bench/bench-report.json` | 每次跑都写的报告 |
| `--update-golden` | — | 用本次实测重写 golden(不跑哈希/性能门禁) |
| `--json` | — | 报告 JSON 同时打到 stdout |
| `--selftest` | — | 自检(sentinel + 短跑),不碰 golden |

非默认 `--scale` / `--ticks` 时哈希与性能门禁都 SKIP(golden 只钉默认规模)。

## 退出码

| 码 | 含义 |
|----|------|
| 0 | 通过(或非默认参数 / baseline 未校准,门禁 SKIP) |
| 1 | 状态哈希与 golden 不符 |
| 2 | 最快趟 nsPerTick > baseline × perfFailMultiplier(baseline ≤ 0 时此码不触发) |
| 3 | sentinel 断言失败 / 参数非法 / golden 缺失或损坏 |
| 4 | 趟间哈希不一致(模拟有非确定性) |

## golden.json

`baselineNsPerTick = 0` 表示"未校准":性能门禁 SKIP,只有哈希门禁生效。

校准流程:合并后,从首次绿色 CI 的 `bench-report` artifact 里读 `minNsPerTick`,
填进 `bench/golden.json` 的 `baselineNsPerTick`,单独提交以激活性能门禁。

故意改了模拟行为、哈希随之变化时:跑 `--update-golden` 重写 golden,
并在 PR 里 review 这个 diff —— 哈希变化必须是有意的。

`bench/bench-report.json` 与 `bench-stdout.txt` 是每次跑的产物,已被 `.gitignore` 排除;
`bench/golden.json` 是版本库里的基线,必须提交。
