namespace Faketorio.Presentation.Core;

/// double 版 2D 向量(表现层坐标;sim 层永远不引入 float/double)。
public readonly record struct Vec2(double X, double Y);

/// 整数 tile 矩形。含 Min,不含 Max(半开区间)。
public readonly record struct RectI(int MinX, int MinY, int MaxX, int MaxY);
