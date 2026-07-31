using Godot;
using MouseKombat.Sim;

// Godot<->sim boundary conversions. The sim is fixed-point (Fix / FixVec2 / SimRect, see
// MouseKombat.Sim/Fix.cs) because it must be bit-identical across machines for rollback netcode
// and replays; the Godot views are float (Vector2/Rect2). Every crossing goes through here, so the
// seam stays one file wide and no float leaks back into the logic.
public static class SimInterop
{
    public static float ToGodot(this Fix f) => (float)f;

    public static Vector2 ToGodot(this FixVec2 v) => new Vector2((float)v.X, (float)v.Y);

    public static Rect2 ToGodot(this SimRect r) =>
        new Rect2((float)r.Position.X, (float)r.Position.Y, (float)r.Size.X, (float)r.Size.Y);

    public static FixVec2 ToSim(this Vector2 v) => FixVec2.FromFloat(v.X, v.Y);

    public static SimRect ToSim(this Rect2 r) =>
        new SimRect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
}
