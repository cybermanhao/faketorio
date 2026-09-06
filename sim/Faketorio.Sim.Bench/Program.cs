// Faketorio 模拟层 benchmark CLI —— 计时协议 + 门禁 + 退出码见 BenchRunner。
using Faketorio.Sim.Bench;

try
{
    return BenchRunner.Run(BenchOptions.Parse(args), Console.Out);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 3;
}
