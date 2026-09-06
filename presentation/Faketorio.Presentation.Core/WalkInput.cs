namespace Faketorio.Presentation.Core;

/// 把四个方向键的按住状态解析成八向行走方向。纯逻辑,零依赖,给 game/ 的
/// PlayerInputController 用。方向约定与 sim 的 Player.WalkDelta 一致:
/// 0=北(-Y),顺时针 —— 1=东北 2=东 3=东南 4=南 5=西南 6=西 7=西北。
public static class WalkInput
{
    /// 返回八向 dir 或 null(无输入,或某轴上下/左右同时按导致抵消且另一轴也无输入)。
    public static int? Resolve(bool up, bool down, bool left, bool right)
    {
        int dx = (right ? 1 : 0) - (left ? 1 : 0);
        int dy = (down ? 1 : 0) - (up ? 1 : 0);   // 屏幕 y 向下:down = +1

        return (dx, dy) switch
        {
            (0, 0)   => null,
            (0, -1)  => 0,
            (1, -1)  => 1,
            (1, 0)   => 2,
            (1, 1)   => 3,
            (0, 1)   => 4,
            (-1, 1)  => 5,
            (-1, 0)  => 6,
            (-1, -1) => 7,
            _ => null,   // 不可达:dx、dy 各 ∈ {-1,0,1},9 种组合已全覆盖
        };
    }
}
