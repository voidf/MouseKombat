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

    // Kept whole so a savestate can rebuild this projectile exactly (see SaveTo / Restore). A
    // projectile outlives the move that fired it, so the spec cannot be recovered from its owner.
    private readonly ProjectileSpec _spec;

    private readonly Fix _speed;
    private readonly Fix _maxDistance;
    private Fix _traveled;
    private readonly SimRect _hitboxLocal;
    private readonly MoveDef _hit;    // synthetic hit data reused by SimPlayer.ApplyDamage
    private int _age;                 // frames alive
    private readonly int _lifeTime;   // 0 = unlimited

    public SimProjectile(int id, int ownerIndex, Vec2 pos, int dir, ProjectileSpec spec)
    {
        Id = id;
        OwnerIndex = ownerIndex;
        Position = pos;
        Dir = dir < 0 ? -1 : 1;
        _spec = spec;
        _speed = spec.Speed;
        _maxDistance = spec.MaxDistance;
        _hitboxLocal = spec.Hitbox;
        _lifeTime = spec.LifeTimeFrame;
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
    public string PrefabId => _spec.PrefabId ?? "";   // the view resolves the scene from this

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
        _age++;
    }

    public bool Expired(Fix cullMinX, Fix cullMaxX)
        => (_lifeTime > 0 && _age >= _lifeTime)
        || (_maxDistance > 0f && _traveled >= _maxDistance)
        || Position.X < cullMinX || Position.X > cullMaxX;

    // ---- savestate ----
    // Identity (id / owner / dir) + the mutable bits + the whole spec, so Restore re-runs the
    // constructor and produces an object identical to the original rather than a half-rebuilt one.
    public void SaveTo(ref SimStateWriter w)
    {
        w.Int(Id);
        w.Int(OwnerIndex);
        w.Int(Dir);
        w.Vec(Position);
        w.Fixed(_traveled);
        w.Int(_age);
        w.Bool(Alive);

        w.Fixed(_spec.Speed);
        w.Vec(_spec.Offset);
        w.Int(_spec.Damage);
        w.Int((int)_spec.Guard);
        w.Fixed(_spec.MaxDistance);
        w.Rect(_spec.Hitbox);
        w.Bool(_spec.CanAirJuggle);
        w.Fixed(_spec.Knockback);
        w.Int(_spec.oH);
        w.Int(_spec.oB);
        w.Int(_spec.LifeTimeFrame);
        w.ShortString(_spec.PrefabId ?? "");
    }

    public static SimProjectile Restore(ref SimStateReader r)
    {
        int id = r.Int();
        int owner = r.Int();
        int dir = r.Int();
        var pos = r.Vec();
        var traveled = r.Fixed();
        int age = r.Int();
        bool alive = r.Bool();

        var spec = new ProjectileSpec
        {
            Speed = r.Fixed(),
            Offset = r.Vec(),
            Damage = r.Int(),
            Guard = (GuardHeight)r.Int(),
            MaxDistance = r.Fixed(),
            Hitbox = r.Rect(),
            CanAirJuggle = r.Bool(),
            Knockback = r.Fixed(),
            oH = r.Int(),
            oB = r.Int(),
            LifeTimeFrame = r.Int(),
            PrefabId = r.ShortString(),
        };

        var p = new SimProjectile(id, owner, pos, dir, spec) { Alive = alive };
        p._traveled = traveled;
        p._age = age;
        return p;
    }
}
