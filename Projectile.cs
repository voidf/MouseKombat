using Godot;

// Visual-only fireball view. Movement, hit detection, and expiry now live in
// MouseKombat.Sim.SimProjectile; GameManager spawns/positions/frees this node to mirror the
// sim projectile by id. The exported Hitbox is kept for debug drawing only (it matches the
// value baked into the character's ProjectileSpec).
public partial class Projectile : Node2D
{
    [Export] public AnimatedSprite2D anim;           // optional; auto-found if null
    [Export] public Rect2 Hitbox = new Rect2(-60, -40, 120, 80); // local; flipped by travel dir (debug draw)
    [Export] public bool DebugDrawBox = true;

    private int _dir = 1;          // +1 right, -1 left

    // Called by GameManager right after instancing. Visual setup only.
    public void Init(int dir)
    {
        _dir = dir < 0 ? -1 : 1;
        if (anim == null) anim = GetNodeOrNull<AnimatedSprite2D>("AnimatedSprite2D");
        if (anim != null)
        {
            anim.FlipH = _dir < 0;
            anim.Play();
        }
    }

    public override void _Process(double delta)
    {
        if (DebugDrawBox) QueueRedraw();
    }

    public override void _Draw()
    {
        if (!DebugDrawBox) return;
        var box = Hitbox;
        if (_dir < 0) box.Position = new Vector2(-box.Position.X - box.Size.X, box.Position.Y);
        DrawRect(box, new Color(1f, 0.5f, 0f, 0.35f), filled: true);
        DrawRect(box, new Color(1f, 0.5f, 0f, 1f), filled: false, width: 2f);
    }
}
