using System;

namespace MouseKombat.Sim;

// Godot-free port of the Projectile logic. GameSim owns a list of these; the Godot view
// mirrors each to a scene node by Id. Hit routing that used to call GameManager.PlayHitFeedback
// is now a HitFeedback entry in StepResult that the view consumes.
public sealed class SimProjectile
{
    public readonly int Id;
    public readonly int OwnerIndex;   // 0 = p1, 1 = p2 (target is the other player)
    public Vec2 Position;
    public readonly int Dir;          // +1 right, -1 left
    public bool Alive = true;

    private readonly Fix _speed;
    private readonly Fix _maxDistance;
    private Fix _traveled;
    private readonly SimRect _hitboxLocal;
    private readonly MoveDef _hit;    // synthetic hit data reused by SimPlayer.ApplyDamage

    public SimProjectile(int id, int ownerIndex, Vec2 pos, int dir, ProjectileSpec spec)
    {
        Id = id;
        OwnerIndex = ownerIndex;
        Position = pos;
        Dir = dir < 0 ? -1 : 1;
        _speed = spec.Speed;
        _maxDistance = spec.MaxDistance;
        _hitboxLocal = spec.Hitbox;
        // non-light so a mid-air target juggles/air-resets like a normal HP hit (matches old Projectile._hit)
        _hit = new MoveDef {
            Damage = spec.Damage,
            Guard = spec.Guard,
            Button = AttackButton.HP,
            CanAirJuggle = spec.CanAirJuggle,
            Knockback = spec.Knockback,
            oH = spec.oH,
            oB = spec.oB,
        };
    }

    public MoveDef Hit => _hit;
    public SimRect HitboxLocal => _hitboxLocal; // for the view's debug draw / flip

    public SimRect GetWorldHitbox()
    {
        var local = _hitboxLocal;
        var pos = local.Position;
        if (Dir < 0) pos = new Vec2(-pos.X - local.Size.X, pos.Y);
        return new SimRect(Position + pos, local.Size);
    }

    public void Advance()
    {
        Fix dx = Dir * _speed * SimPlayer.Dt;
        Position += new Vec2(dx, 0);
        _traveled += Fix.Abs(dx);
    }

    public bool Expired(Fix cullMinX, Fix cullMaxX)
        => _traveled >= _maxDistance || Position.X < cullMinX || Position.X > cullMaxX;
}
