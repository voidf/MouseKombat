using Godot;
using MouseKombat.Sim;

// Thin Godot VIEW over a SimPlayer. It: (1) exposes [Export] tuning that becomes the sim's
// PlayerConfig, (2) turns its IInputSource into an InputFrame the sim consumes, (3) replays
// the sim's per-frame AnimCommands onto the AnimatedSprite2D, and (4) does presentation-only
// animation sync (looping locomotion, attack tail, juggle clip swap) + debug draw, reading
// sim state and never writing logic. All combat logic lives in MouseKombat.Sim.SimPlayer.
public partial class Player : Node2D
{
    [Export] public AnimatedSprite2D anim;

    [Export] public string ActionLeft = "p1_left";
    [Export] public string ActionRight = "p1_right";
    [Export] public string ActionUp = "p1_up";
    [Export] public string ActionDown = "p1_down";
    [Export] public string InputPrefix = "p1"; // 6 attack buttons resolve to {prefix}_lp.._hk

    // Device binding injected by GameManager from the ready screen. When null (e.g. MFEntry
    // opened directly in the editor) input falls back to the InputMap actions above.
    public IInputSource Source;

    // The logic object this view mirrors; created + bound by GameManager after the sim exists.
    public SimPlayer Sim;

    [Export] public string IdleAnimName = "IDLE";
    [Export] public string WalkAnimName = "WALK";
    // when on, walking backward (away from the opponent) plays WALK in reverse; off = always forward.
    [Export] public bool ReverseWalkBackward = false;
    [Export] public string HurtAnimName = "HURT";
    [Export] public string DefAnimName = "DEF";              // standing block-hit
    [Export] public string CrouchDefAnimName = "CROUCHDEF";  // crouching block-hit
    [Export] public string JumpAnimName = "JUMP";
    [Export] public string EnterCrouchAnimName = "ENTER_CROUCH"; // transition; exit = played backwards
    [Export] public string CrouchIdleAnimName = "CROUCH";        // steady held crouch pose
    [Export] public string LaunchRiseAnimName = "LAUNCH";   // juggle, rising
    [Export] public string FallAnimName = "FALL";           // juggle, falling
    [Export] public string KnockdownAnimName = "KNOCKDOWN"; // grounded after juggle (invincible)
    [Export] public string WakeupAnimName = "WAKEUP";       // getting up (invincible until last frame)
    [Export] public string AirHurtAnimName = "AIR_HURT";    // air reset (light air hit, lands on feet)

    [Export] public float DefDamageMultiplier = 0.1f;

    [Export] public bool ArtFacesRight = false;
    [Export] public bool StartFacingRight = true;

    [Export] public int MaxHp = 100;
    [Export] public float WalkSpeedPxPerSec = 220f;

    [Export] public CharacterId Character = CharacterId.Hamster; // selects the C# move table

    [ExportGroup("Hurtboxes (standing)")]
    [Export] public Rect2 HeadBox = new Rect2(-40, -200, 80, 55);
    [Export] public Rect2 BodyBox = new Rect2(-55, -150, 110, 95);
    [Export] public Rect2 ArmsBox = new Rect2(-65, -165, 130, 60);
    [Export] public Rect2 LegsBox = new Rect2(-45, -70, 90, 70);
    [ExportGroup("Hurtboxes (crouch)")]
    [Export] public Rect2 CrouchHeadBox = new Rect2(-15, -110, 80, 55); // lowered + slightly forward
    [Export] public Rect2 CrouchBodyBox = new Rect2(-55, -75, 110, 75);
    [Export] public Rect2 CrouchArmsBox = new Rect2(-60, -90, 120, 55);
    [Export] public Rect2 CrouchLegsBox = new Rect2(-45, -35, 90, 35);
    [ExportGroup("")]

    [Export] public int HurtStunFrames = 14;
    [Export] public int DefHitStunFrames = 10;
    [Export] public int CrouchEnterFrames = 8; // logic duration of enter/exit; set to match ENTER_CROUCH anim length

    [Export] public int DownedFrames = 30;     // grounded knockdown wait before wakeup (invincible)
    [Export] public int DownedMinFrames = 12;  // earliest an input may trigger wakeup
    [Export] public int WakeupFrames = 24;     // wakeup anim length (invincible until its last frame)
    [Export] public float AirResetPop = 350f;  // small upward pop on a light air hit (air reset)

    [Export] public float JumpVelocity = 1350f;
    [Export] public float Gravity = 3600f;
    [Export] public float ForwardJumpSpeed = 420f;
    [Export] public float BackJumpSpeed = 380f;

    [Export] public PackedScene ProjectileScene; // fireball scene spawned by motion specials (GameManager reads)

    [Export] public bool DebugDrawBoxes = true;

    private bool _walkPlayingBack = false; // tracks WALK reverse playback so we don't retrigger each frame

    // Build the sim config from the exported tuning. GameManager overrides StartPos/facing
    // with its own P1/P2 start values to match the original reset convention.
    public PlayerConfig BuildConfig() => new PlayerConfig
    {
        Character = Character,
        StartPos = new System.Numerics.Vector2(Position.X, Position.Y),
        StartFacingRight = StartFacingRight,
        MaxHp = MaxHp,
        WalkSpeedPxPerSec = WalkSpeedPxPerSec,
        DefDamageMultiplier = DefDamageMultiplier,
        HurtStunFrames = HurtStunFrames,
        DefHitStunFrames = DefHitStunFrames,
        CrouchEnterFrames = CrouchEnterFrames,
        DownedFrames = DownedFrames,
        DownedMinFrames = DownedMinFrames,
        WakeupFrames = WakeupFrames,
        AirResetPop = AirResetPop,
        JumpVelocity = JumpVelocity,
        Gravity = Gravity,
        ForwardJumpSpeed = ForwardJumpSpeed,
        BackJumpSpeed = BackJumpSpeed,
        HeadBox = HeadBox.ToSim(),
        BodyBox = BodyBox.ToSim(),
        ArmsBox = ArmsBox.ToSim(),
        LegsBox = LegsBox.ToSim(),
        CrouchHeadBox = CrouchHeadBox.ToSim(),
        CrouchBodyBox = CrouchBodyBox.ToSim(),
        CrouchArmsBox = CrouchArmsBox.ToSim(),
        CrouchLegsBox = CrouchLegsBox.ToSim(),
        IdleAnimName = IdleAnimName,
        WalkAnimName = WalkAnimName,
        HurtAnimName = HurtAnimName,
        DefAnimName = DefAnimName,
        CrouchDefAnimName = CrouchDefAnimName,
        JumpAnimName = JumpAnimName,
        EnterCrouchAnimName = EnterCrouchAnimName,
        CrouchIdleAnimName = CrouchIdleAnimName,
        LaunchRiseAnimName = LaunchRiseAnimName,
        FallAnimName = FallAnimName,
        KnockdownAnimName = KnockdownAnimName,
        WakeupAnimName = WakeupAnimName,
        AirHurtAnimName = AirHurtAnimName,
    };

    public void Bind(SimPlayer sim)
    {
        Sim = sim;
        SyncFromSim(); // apply initial position + drain the ctor's IDLE command
    }

    // Poll this view's device into one InputFrame for the sim. No poll while Dead (matches the
    // old LatchInput early-return); the sim also guards the Dead case.
    public InputFrame BuildInputFrame()
    {
        if (Sim != null && Sim.State == PlayerState.Dead) return InputFrame.Neutral;

        if (Source != null)
        {
            Source.Poll();
            int mask = 0;
            foreach (var b in Source.JustPressedButtons) mask |= 1 << (int)b;
            return new InputFrame(Source.Left, Source.Right, Source.Up, Source.Down, mask);
        }

        bool l = Input.IsActionPressed(ActionLeft);
        bool r = Input.IsActionPressed(ActionRight);
        bool u = Input.IsActionPressed(ActionUp);
        bool d = Input.IsActionPressed(ActionDown);
        return new InputFrame(l, r, u, d, ReadPressedMask());
    }

    // InputMap fallback: bitset of the 6 attack buttons just-pressed this frame (LP+LK throws detectable)
    private int ReadPressedMask()
    {
        int m = 0;
        if (Input.IsActionJustPressed(InputPrefix + "_lp")) m |= 1 << (int)AttackButton.LP;
        if (Input.IsActionJustPressed(InputPrefix + "_mp")) m |= 1 << (int)AttackButton.MP;
        if (Input.IsActionJustPressed(InputPrefix + "_hp")) m |= 1 << (int)AttackButton.HP;
        if (Input.IsActionJustPressed(InputPrefix + "_lk")) m |= 1 << (int)AttackButton.LK;
        if (Input.IsActionJustPressed(InputPrefix + "_mk")) m |= 1 << (int)AttackButton.MK;
        if (Input.IsActionJustPressed(InputPrefix + "_hk")) m |= 1 << (int)AttackButton.HK;
        return m;
    }

    // Push sim state into the view: node position + replay the frame's animation commands.
    // Called by GameManager right after each sim.Step (and after a round reset).
    public void SyncFromSim()
    {
        if (Sim == null) return;
        Position = new Vector2(Sim.Position.X, Sim.Position.Y);

        var events = Sim.AnimEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var c = events[i];
            switch (c.Kind)
            {
                case AnimKind.Play: PlayAnimSafe(c.Name); break;
                case AnimKind.PlayRestart: PlayAnimSafe(c.Name, true); break;
                case AnimKind.PlayBackwards: PlayAnimBackwardsSafe(c.Name); break;
                case AnimKind.Stop: anim?.Stop(); break;
            }
        }
        events.Clear();
    }

    public override void _Process(double delta)
    {
        if (Sim == null) return;

        // presentation only — observes logic State, never writes it
        anim.FlipH = ArtFacesRight ? !Sim.FacingRight : Sim.FacingRight;

        // keep the looping locomotion clip in sync with State (combat/jump/crouch clips
        // are one-shots fired by the logic tick at their transition, replayed in SyncFromSim)
        if (Sim.State == PlayerState.Idle)
        {
            // attack tail: logic over but the (longer) attack clip may still be playing —
            // let it finish; switch to IDLE only once it stops.
            bool atkTailPlaying = anim.IsPlaying() && anim.Animation == Sim.CurrentAtkAnim && !string.IsNullOrEmpty(Sim.CurrentAtkAnim);
            if (!atkTailPlaying && anim.Animation != IdleAnimName) PlayAnimSafe(IdleAnimName);
        }
        else if (Sim.State == PlayerState.Walk)
        {
            // backward = moving away from the opponent (same test as block input).
            bool back = ReverseWalkBackward && Sim.IsDefendingInput;
            if (anim.Animation != WalkAnimName || _walkPlayingBack != back)
            {
                if (back) PlayAnimBackwardsSafe(WalkAnimName);
                else PlayAnimSafe(WalkAnimName);
                _walkPlayingBack = back;
            }
        }
        else if (Sim.State == PlayerState.Crouch)
        {
            // ENTER_CROUCH transition plays once, then settle on the held CROUCH pose.
            bool entering = anim.IsPlaying() && anim.Animation == EnterCrouchAnimName;
            if (!entering && anim.Animation != CrouchIdleAnimName) PlayAnimSafe(CrouchIdleAnimName);
        }
        else if (Sim.State == PlayerState.Juggle)
        {
            // rising -> LAUNCH clip; once gravity pulls down -> FALL clip
            string want = Sim.Vy < 0f ? LaunchRiseAnimName : FallAnimName;
            if (anim.Animation != want) PlayAnimSafe(want);
        }

        if (DebugDrawBoxes) QueueRedraw();
    }

    public override void _Draw()
    {
        if (!DebugDrawBoxes || Sim == null) return;

        // hurt regions (local space; flip X by facing to match the sim's ToWorld)
        DrawHurtRegion(HurtRegion.Head, new Color(0.2f, 1f, 0.4f));
        DrawHurtRegion(HurtRegion.Body, new Color(0f, 0.8f, 1f));
        DrawHurtRegion(HurtRegion.Arms, new Color(1f, 0.9f, 0.2f));
        DrawHurtRegion(HurtRegion.Legs, new Color(0.8f, 0.4f, 1f));

        // hitbox: only while attacking (debug viz for every move)
        if (Sim.State == PlayerState.Attack)
        {
            var hb = Sim.CurHitboxLocal.ToGodot();
            if (!Sim.FacingRight) hb.Position = new Vector2(-hb.Position.X - hb.Size.X, hb.Position.Y);

            bool active = Sim.IsAttackingActive;
            Color fill = active ? new Color(1, 0, 0, 0.45f) : new Color(1, 0.6f, 0, 0.25f);
            Color edge = active ? new Color(1, 0, 0, 1f) : new Color(1, 0.6f, 0, 1f);
            DrawRect(hb, fill, filled: true);
            DrawRect(hb, edge, filled: false, width: 2f);
        }

        DrawCircle(Vector2.Zero, 3f, new Color(1, 1, 1, 1));
    }

    private void DrawHurtRegion(HurtRegion r, Color c)
    {
        var box = Sim.RegionLocal(r).ToGodot();
        if (!Sim.FacingRight) box.Position = new Vector2(-box.Position.X - box.Size.X, box.Position.Y);
        DrawRect(box, new Color(c.R, c.G, c.B, 0.18f), filled: true);
        DrawRect(box, new Color(c.R, c.G, c.B, 0.9f), filled: false, width: 2f);
    }

    private void PlayAnimSafe(string name, bool forceRestart = false)
    {
        if (anim?.SpriteFrames == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (!anim.SpriteFrames.HasAnimation(name)) return;
        // 如果强制重开且当前已经在播放同一动画，先 Stop 以清除 Godot 的播放状态缓存
        if (forceRestart && anim.Animation == name)
        {
            anim.Stop();
        }

        anim.Play(name);

        // 确保帧数和计时进度彻底归零（针对 Godot 4 最稳妥的双重保险）
        if (forceRestart)
        {
            anim.Frame = 0;
            anim.FrameProgress = 0f;
        }
    }

    private void PlayAnimBackwardsSafe(string name)
    {
        if (anim?.SpriteFrames == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (!anim.SpriteFrames.HasAnimation(name)) return;
        anim.PlayBackwards(name);
    }
}
