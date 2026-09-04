namespace Faketorio.Sim;

// 一份合成任务:配方 id + 剩余份数 + 当前这份的进度(int64 定点累加,§6.2)。
public record struct CraftJob(int RecipeProtoId, int Count, long Progress);
