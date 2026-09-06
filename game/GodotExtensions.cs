using Godot;
using CoreVec2 = Faketorio.Presentation.Core.Vec2;

namespace Faketorio.Game;

public static class GodotExtensions
{
    public static Vector2 ToGodot(this CoreVec2 v) => new((float)v.X, (float)v.Y);
    public static CoreVec2 ToCore(this Vector2 v) => new(v.X, v.Y);
}
