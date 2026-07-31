using Godot;
using MouseKombat.Sim;

// Thin Godot VIEW over a SimPlayer. It: (1) exposes [Export] tuning that becomes the sim's
// PlayerConfig, (2) turns its IInputSource into an InputFrame the sim consumes, (3) drives the
// AnimatedSprite2D one sprite frame per LOGIC frame (see TickAnimation — Godot never plays the
// clip itself, so what's on screen is a pure function of sim state), and (4) debug-draws the
// boxes. It reads sim state and never writes logic; all combat logic lives in SimPlayer.
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

    // Optional AI/policy driver. When set, GameManager asks it for this player's InputFrame
    // instead of polling the device (Source). Bound from the ready screen's AI menu.
    public IAgent Agent;

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

    // Corner pushback: share of a hit's knockback that a stage wall swallowed which gets handed
    // back to THIS player (the attacker), shoving it away from a cornered opponent. Stops fast
    // normals from looping someone in the corner. 1 = full, 0 = old behavior.
    [Export] public float CornerPushbackScale = 1f;

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

    // Y offset (world px, negative = up) where the match director puts this fighter's name tag.
    // Per-character because the sprites differ in height.
    [Export] public float TagOffsetY = -250f;

    [Export] public bool DebugDrawBoxes = true;

    // Build the sim config from the exported tuning. GameManager overrides StartPos/facing
    // with its own P1/P2 start values to match the original reset convention.
    public PlayerConfig BuildConfig() => new PlayerConfig
    {
        Character = Character,
        StartPos = Position.ToSim(),
        StartFacingRight = StartFacingRight,
        MaxHp = MaxHp,
        WalkSpeedPxPerSec = WalkSpeedPxPerSec,
        DefDamageMultiplier = DefDamageMultiplier,
        CornerPushbackScale = CornerPushbackScale,
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
        // Godot must never advance the clip itself — TickAnimation writes the frame explicitly.
        anim?.Stop();
        SyncFromSim();   // initial position + the ctor's IDLE command
        TickAnimation(); // show frame 0 immediately rather than a blank first tick
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

    // Push sim state into the view: node position + this frame's animation commands.
    // Called by GameManager right after each sim.Step (and after a round reset).
    public void SyncFromSim()
    {
        if (Sim == null) return;
        Position = Sim.Position.ToGodot();

        // The sim's AnimCommands decide WHICH clip; TickAnimation decides which FRAME of it.
        // Two of these choices are not recoverable from public sim state — the standing-vs-crouching
        // block clip, and a throw victim's pose (which comes from the ATTACKER's bind timeline) —
        // so the event stream stays the authority on clip selection.
        var events = Sim.AnimEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var c = events[i];
            switch (c.Kind)
            {
                case AnimKind.Play: SetClip(c.Name, restart: false, reverse: false); break;
                case AnimKind.PlayRestart: SetClip(c.Name, restart: true, reverse: false); break;
                case AnimKind.PlayBackwards: SetClip(c.Name, restart: true, reverse: true); break;
                // Stop is only ever emitted at Hp == 0, and TickAnimation derives the death
                // freeze from PlayerState.Dead instead. Latching a flag here would strand the
                // view: the round reset re-emits Play(IDLE), which is a no-op when IDLE was
                // already the displayed clip, so the flag would never be cleared.
                case AnimKind.Stop: break;
            }
        }
        events.Clear();
    }

    // ================= animation: one sprite frame per LOGIC frame =================
    // AnimatedSprite2D normally advances itself on the RENDER clock, while the sim advances on the
    // physics tick. Two independent clocks means the sprite shown for a given AtkFrame varies with
    // framerate and stutter, and after a rollback re-simulation it would be wrong outright. So we
    // never let Godot play the clip: each logic tick we compute the exact sprite frame and write it.
    //
    // The per-clip timeline is built ONCE from the SpriteFrames data (per-animation fps + per-frame
    // duration), converted into "how many logic frames does sprite frame i occupy". That honors the
    // art as authored: attack clips at speed 60 advance 1:1 with frame data, while IDLE at speed 10
    // holds each frame for 6 logic frames, and Kangaroo's 32 fps AtkU stretches over ~9.4.
    //
    // Rollback (期3) then only needs these three fields saved alongside the sim state: the clip
    // name, the logic frame within it, and the reverse flag.
    private sealed class ClipTimeline
    {
        public int[] FrameAt;   // index = logic frames since the clip started -> sprite frame
        public bool Loop;
        public int LogicLength => FrameAt.Length;
    }

    private readonly System.Collections.Generic.Dictionary<string, ClipTimeline> _timelines = new();
    private string _clip = "";
    private int _clipFrame;      // logic frames elapsed in _clip
    private bool _clipReverse;

    private ClipTimeline Timeline(string clip)
    {
        if (string.IsNullOrEmpty(clip)) return null;
        if (_timelines.TryGetValue(clip, out var cached)) return cached;

        ClipTimeline built = null;
        var sf = anim?.SpriteFrames;
        if (sf != null && sf.HasAnimation(clip))
        {
            int n = sf.GetFrameCount(clip);
            if (n > 0)
            {
                double fps = sf.GetAnimationSpeed(clip);
                if (fps <= 0.0) fps = LogicFps;                 // 0 fps = a single held pose
                var map = new System.Collections.Generic.List<int>(n * 4);
                for (int i = 0; i < n; i++)
                {
                    // GetFrameDuration is a multiplier on 1/fps, so seconds = duration / fps.
                    double logicFrames = LogicFps * sf.GetFrameDuration(clip, i) / fps;
                    int hold = Mathf.Max(1, Mathf.RoundToInt((float)logicFrames));
                    for (int k = 0; k < hold; k++) map.Add(i);
                }
                built = new ClipTimeline { FrameAt = map.ToArray(), Loop = sf.GetAnimationLoop(clip) };
            }
        }
        _timelines[clip] = built; // cache misses too: a missing clip must not re-probe every frame
        return built;
    }

    private static float LogicFps => Engine.PhysicsTicksPerSecond;

    // Switch the displayed clip. A missing clip is ignored (keeps the current pose), matching the
    // old PlayAnimSafe behavior — that is what lets a character ship with partial art.
    private void SetClip(string clip, bool restart, bool reverse)
    {
        if (Timeline(clip) == null) return;
        if (!restart && _clip == clip && _clipReverse == reverse) return;
        _clip = clip;
        _clipFrame = 0;
        _clipReverse = reverse;
    }

    // Advance the animation by exactly one logic frame. Called every physics tick by GameManager —
    // including while the match is paused on a win, so the fighters keep breathing.
    public void TickAnimation()
    {
        if (Sim == null || anim == null) return;

        anim.FlipH = ArtFacesRight ? !Sim.FacingRight : Sim.FacingRight;

        // Dead: hold whatever pose was on screen when the KO landed. Derived from sim STATE, not
        // latched from the AnimKind.Stop event, so a round reset — which just moves State back to
        // Idle — always restores animation on its own.
        if (Sim.State == PlayerState.Dead) return;

        ReconcileSteadyStateClip();

        var t = Timeline(_clip);
        if (t == null) return;

        int i = _clipFrame;
        if (t.Loop) i %= t.LogicLength;
        else if (i >= t.LogicLength) i = t.LogicLength - 1;   // one-shot: hold the last frame

        int sprite = _clipReverse ? t.FrameAt[t.LogicLength - 1 - i] : t.FrameAt[i];
        if (anim.Animation != _clip) anim.Animation = _clip;
        anim.SetFrameAndProgress(sprite, 0f);

        _clipFrame++;
        // keep a looping clip's counter bounded: it is part of the view state a rollback has to
        // save/restore, and an unbounded counter would drift toward int overflow while idling
        if (t.Loop && _clipFrame >= t.LogicLength) _clipFrame -= t.LogicLength;
    }

    // Clips the sim does NOT emit an event for, because they are steady-state rather than a
    // transition: looping locomotion, the settle after a transition clip, and the juggle rise/fall
    // swap. Evaluated from logic state on the logic tick, so it stays frame-exact.
    private void ReconcileSteadyStateClip()
    {
        // Overflow frames are authored on purpose (the artist sets per-frame durations in the
        // editor), so a clip that runs LONGER than its move keeps playing until something actually
        // interrupts it. Only the locomotion states defer like this; every other state change
        // arrives with its own AnimCommand from the sim, which replaces the clip outright.
        if ((Sim.State == PlayerState.Idle || Sim.State == PlayerState.Walk) && AttackTailRunning())
            return;

        switch (Sim.State)
        {
            case PlayerState.Idle:
                SetClip(IdleAnimName, restart: false, reverse: false);
                break;

            case PlayerState.Walk:
                // backward = moving away from the opponent (same test as the block input)
                SetClip(WalkAnimName, restart: false,
                    reverse: ReverseWalkBackward && Sim.IsDefendingInput);
                break;

            case PlayerState.Crouch:
                // ENTER_CROUCH plays once, then settle on the held CROUCH pose
                if (_clip == EnterCrouchAnimName)
                {
                    var ec = Timeline(_clip);
                    if (ec != null && _clipFrame < ec.LogicLength) return;
                }
                SetClip(CrouchIdleAnimName, restart: false, reverse: false);
                break;

            case PlayerState.Juggle:
                // rising -> LAUNCH; once gravity wins -> FALL
                SetClip(Sim.Vy < 0f ? LaunchRiseAnimName : FallAnimName,
                    restart: false, reverse: false);
                break;
        }
    }

    // True while the last attack's clip still has authored frames left to show. CurrentAtkAnim
    // stays set after a move ends, which is what lets the overflow outlive the move itself.
    private bool AttackTailRunning()
    {
        if (string.IsNullOrEmpty(Sim.CurrentAtkAnim) || _clip != Sim.CurrentAtkAnim) return false;
        var t = Timeline(_clip);
        return t != null && !t.Loop && _clipFrame < t.LogicLength;
    }

    public override void _Process(double delta)
    {
        if (Sim == null) return;
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
}
