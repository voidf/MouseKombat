using System;
using System.Collections.Generic;

namespace MouseKombat.Sim;

// ---- replay playback: deterministic re-simulation with seekable keyframes ----
//
// Playing forward is just Step() with the recorded inputs. The interesting part is seeking, which the
// player needs for the scrub bar, for single-frame stepping and for reverse playback: there is no way
// to run the sim backwards, so a seek to an earlier frame restores the nearest keyframe at or before
// the target and re-simulates forward from there.
//
// The keyframes are GameSim savestates (see GameSim.SaveState), which is why the savestate work came
// first: rollback netcode and this share exactly the same machinery. At 60 frames of spacing a
// backward step costs at most 60 sim steps, and the sim runs over a million steps per second, so
// frame-by-frame reverse playback is far cheaper than the 60 Hz it has to keep up with.
//
// Godot-free on purpose: the whole thing is testable headless, and the same code can drive a
// Python-side replay validator.
public sealed class ReplaySession
{
    private readonly ReplayData _data;
    private readonly GameSim _sim;

    // frame index -> savestate bytes. Keyframe 0 is the pristine start, so a seek can always land.
    private readonly Dictionary<int, byte[]> _keyframes = new();
    private readonly int _keyframeSpacing;

    public GameSim Sim => _sim;
    public ReplayData Data => _data;

    // Frames already applied: 0 means "nothing stepped yet", TotalFrames means "played to the end".
    public int Frame { get; private set; }
    public int TotalFrames => _data.FrameCount;
    public bool AtEnd => Frame >= TotalFrames;

    // The two configs come from the CALLER, not the file: the replay stores which characters played,
    // and the caller rebuilds their tuning from the current build (see the note in ReplayData about
    // why per-match tuning is deliberately not persisted).
    public ReplaySession(ReplayData data, PlayerConfig c1, PlayerConfig c2, int keyframeSpacing = 60)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _keyframeSpacing = Math.Max(1, keyframeSpacing);

        c1.Character = data.P1Char;
        c1.SetStart(data.P1StartX, data.P1StartY, facingRight: true);
        c2.Character = data.P2Char;
        c2.SetStart(data.P2StartX, data.P2StartY, facingRight: false);

        _sim = new GameSim(c1, c2, data.StageMinX, data.StageMaxX, data.WorldWidth);
        Frame = 0;
        StoreKeyframe(0);
    }

    private void StoreKeyframe(int frame)
    {
        if (_keyframes.ContainsKey(frame)) return;
        _keyframes[frame] = _sim.SaveStateBytes();
    }

    // Advance exactly one recorded frame. Returns false at the end of the recording.
    public bool StepForward()
    {
        if (AtEnd) return false;
        _sim.Step(_data.P1At(Frame), _data.P2At(Frame));
        Frame++;
        if (Frame % _keyframeSpacing == 0) StoreKeyframe(Frame);
        return true;
    }

    // Seek to any frame. Forward seeks just keep stepping; backward seeks restore a keyframe first,
    // which is the only way to move back through a simulation that cannot run in reverse.
    public void SeekTo(int frame)
    {
        frame = Math.Clamp(frame, 0, TotalFrames);
        if (frame == Frame) return;

        if (frame < Frame)
        {
            int from = NearestKeyframeAtOrBefore(frame);
            _sim.LoadStateFrom(_keyframes[from]);
            Frame = from;
        }
        while (Frame < frame) StepForward();
    }

    public bool StepBackward()
    {
        if (Frame <= 0) return false;
        SeekTo(Frame - 1);
        return true;
    }

    private int NearestKeyframeAtOrBefore(int frame)
    {
        int best = 0;
        foreach (int k in _keyframes.Keys)
            if (k <= frame && k > best) best = k;
        return best;
    }

    public void Restart() => SeekTo(0);

    // ---- integrity ----
    // Plays the whole recording and compares the end state against the checksum stored when it was
    // recorded. A mismatch means this build no longer simulates the recorded inputs the same way —
    // almost always because character tuning or frame data changed since. Callers should surface that
    // rather than silently presenting a match that never happened.
    public bool Verify(out uint expected, out uint actual)
    {
        int resume = Frame;
        SeekTo(TotalFrames);
        actual = _sim.Checksum();
        expected = _data.FinalChecksum;
        SeekTo(resume);
        return expected == 0 || expected == actual;   // 0 = recorded without a checksum
    }

    public int KeyframeCount => _keyframes.Count;
}
