using Godot;

// Fireball-style projectile. One script shared by csProjectile.tscn / dsProjectile.tscn.
// Hitbox is an exported Rect2 so it can be tuned directly in each tscn's Inspector,
// consistent with the game's custom (non-physics) Rect2 hit system.
public partial class Projectile : Node2D
{
    [Export] public AnimatedSprite2D anim;           // optional; auto-found if null
    [Export] public Rect2 Hitbox = new Rect2(-60, -40, 120, 80); // local; flipped by travel dir
    [Export] public bool DebugDrawBox = true;

    private float _speed;
    private int _dir = 1;          // +1 right, -1 left
    private float _maxDistance = 900f;
    private float _traveled = 0f;
    private Player _target;
    private MoveDef _hit;          // synthetic hit data reused by Player.ApplyDamage
    private bool _dead = false;

    // Called by the spawner right after instancing.
    public void Init(int dir, ProjectileSpec spec, Player target)
    {
        _dir = dir < 0 ? -1 : 1;
        _speed = spec.Speed;
        _maxDistance = spec.MaxDistance;
        _target = target;
        _hit = new MoveDef { Damage = spec.Damage, Guard = spec.Guard, Button = AttackButton.HP }; // non-light: no air-reset, no juggle

        if (anim == null) anim = GetNodeOrNull<AnimatedSprite2D>("AnimatedSprite2D");
        if (anim != null)
        {
            anim.FlipH = _dir < 0;
            anim.Play();
        }
        OnSpawned();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_dead) return;

        float dx = _dir * _speed * (float)delta;
        Position += new Vector2(dx, 0);
        _traveled += Mathf.Abs(dx);

        if (DebugDrawBox) QueueRedraw();

        // hit the opponent (skip dead / invincible — same rule as melee)
        if (_target != null && _target.State != Player.PlayerState.Dead && !_target.IsInvincible
            && _target.HurtboxOverlaps(GetWorldHitbox()))
        {
            _target.ApplyDamage(_hit, _dir);
            OnHit();
            Destroy();
            return;
        }

        // expire off-screen / past max travel
        float vw = GetViewport().GetVisibleRect().Size.X;
        float x = GlobalPosition.X;
        if (_traveled >= _maxDistance || x < -200f || x > vw + 200f)
        {
            OnExpired();
            Destroy();
        }
    }

    public Rect2 GetWorldHitbox()
    {
        var local = Hitbox;
        var pos = local.Position;
        if (_dir < 0) pos = new Vector2(-pos.X - local.Size.X, pos.Y);
        return new Rect2(GlobalPosition + pos, local.Size);
    }

    private void Destroy()
    {
        _dead = true;
        QueueFree();
    }

    // ---- presentation hooks: wire particles / SFX here later ----
    private void OnSpawned() { /* TODO: spawn VFX, play launch SFX */ }
    private void OnHit() { /* TODO: hit spark VFX, impact SFX */ }
    private void OnExpired() { /* TODO: fizzle VFX */ }

    public override void _Draw()
    {
        if (!DebugDrawBox) return;
        var box = Hitbox;
        if (_dir < 0) box.Position = new Vector2(-box.Position.X - box.Size.X, box.Position.Y);
        DrawRect(box, new Color(1f, 0.5f, 0f, 0.35f), filled: true);
        DrawRect(box, new Color(1f, 0.5f, 0f, 1f), filled: false, width: 2f);
    }
}
