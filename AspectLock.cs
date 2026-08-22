using Godot;

// Autoload singleton. Keeps the OS window locked to the game's 4:3 base aspect
// (800x600) while the user drags any window edge, so the "keep" stretch produces
// no letterbox bars — the whole 800x600 canvas (UI, world sprites, and the
// Node2D debug boxes drawn in world space) scales up as one unit. Logic runs in
// the fixed 800x600 space regardless of window size, so gameplay is unaffected.
public partial class AspectLock : Node
{
    private const float BaseW = 800f;
    private const float BaseH = 600f;
    private const float Aspect = BaseW / BaseH; // 4:3

    // don't let it shrink so far the game is unusable
    private static readonly Vector2I MinSize = new Vector2I(400, 300);

    private bool _adjusting;      // guards against our own resize re-triggering SizeChanged
    private Vector2I _last;       // previous window size, to tell which edge the user dragged

    // The editor suspends the lock while it is open: it disables content scaling entirely, so a
    // free-form window is exactly what it wants.
    public static AspectLock Instance { get; private set; }
    public bool Suspended;

    public override void _Ready()
    {
        Instance = this;
        var win = GetWindow();
        win.MinSize = MinSize;
        _last = win.Size;
        win.SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged()
    {
        if (_adjusting || Suspended) return;

        var win = GetWindow();
        Vector2I size = win.Size;

        // Lock the axis the user changed the LEAST — i.e. derive the smaller-delta
        // axis from the one they're actively dragging.
        int dw = Mathf.Abs(size.X - _last.X);
        int dh = Mathf.Abs(size.Y - _last.Y);

        Vector2I target = dw >= dh
            ? new Vector2I(size.X, Mathf.RoundToInt(size.X / Aspect))   // width dragged -> fit height
            : new Vector2I(Mathf.RoundToInt(size.Y * Aspect), size.Y);  // height dragged -> fit width

        target.X = Mathf.Max(target.X, MinSize.X);
        target.Y = Mathf.Max(target.Y, MinSize.Y);

        if (target != size)
        {
            _adjusting = true;
            win.Size = target;
            _adjusting = false;
        }
        _last = win.Size;
    }
}
