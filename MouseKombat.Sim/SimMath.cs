using System;

namespace MouseKombat.Sim;

// Godot-free math for the headless sim.
//
// Vec2 is FixVec2 (see Fix.cs) — fixed-point, so results are bit-identical on every machine.
// SimRect ports ONLY the Godot Rect2 members the combat logic uses, with the same semantics
// (see each method) so hit detection stays parity-exact after the extraction.
public static class SimMath
{
    // component-wise min/max (Godot Vector2.Min / Vector2.Max)
    public static Vec2 Min(Vec2 a, Vec2 b) => new Vec2(Fix.Min(a.X, b.X), Fix.Min(a.Y, b.Y));
    public static Vec2 Max(Vec2 a, Vec2 b) => new Vec2(Fix.Max(a.X, b.X), Fix.Max(a.Y, b.Y));

    // Godot Mathf.RoundToInt: banker's rounding (ToEven). Kept exact because blocked-damage
    // rounding depends on it. See Fix.RoundToInt.
    public static int RoundToInt(Fix x) => x.RoundToInt();
}

public struct SimRect
{
    public Vec2 Position;
    public Vec2 Size;

    public SimRect(Fix x, Fix y, Fix w, Fix h)
    {
        Position = new Vec2(x, y);
        Size = new Vec2(w, h);
    }

    public SimRect(Vec2 position, Vec2 size)
    {
        Position = position;
        Size = size;
    }

    // Godot Rect2.GetCenter(): position + size * 0.5
    public Vec2 GetCenter() => Position + Size * Fix.Half;

    // Godot Rect2.Intersects(b, includeBorders = false): strict interior overlap.
    public bool Intersects(SimRect b)
    {
        return Position.X < b.Position.X + b.Size.X
            && Position.X + Size.X > b.Position.X
            && Position.Y < b.Position.Y + b.Size.Y
            && Position.Y + Size.Y > b.Position.Y;
    }

    // Godot Rect2.Merge(b): smallest rect containing both.
    public SimRect Merge(SimRect b)
    {
        Vec2 beg = SimMath.Min(Position, b.Position);
        Vec2 end = SimMath.Max(Position + Size, b.Position + b.Size);
        return new SimRect(beg, end - beg);
    }

    // Godot Rect2.Intersection(b): the overlap rect, or a zero rect if none.
    public SimRect Intersection(SimRect b)
    {
        if (!Intersects(b)) return new SimRect(Vec2.Zero, Vec2.Zero);
        Vec2 pos = SimMath.Max(Position, b.Position);
        Vec2 end = SimMath.Min(Position + Size, b.Position + b.Size);
        return new SimRect(pos, end - pos);
    }
}
