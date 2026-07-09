using Godot;
using MouseKombat.Sim;

// Godot<->sim boundary conversions. The sim uses System.Numerics/SimRect; the Godot views
// use Godot.Vector2/Rect2. These adapters convert at the seam. (Temporary consumers inside
// Player are removed once its logic moves into SimPlayer; the seam itself stays for views.)
public static class SimInterop
{
    public static Rect2 ToGodot(this SimRect r) =>
        new Rect2(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);

    public static Vector2 ToGodot(this System.Numerics.Vector2 v) => new Vector2(v.X, v.Y);

    public static SimRect ToSim(this Rect2 r) =>
        new SimRect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
}
