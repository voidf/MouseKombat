using System.Collections.Generic;
using System.Linq;
using MouseKombat.Sim;

internal static partial class Program
{
    private static void EditorFrameTests()
    {
        var source = new HeroFrame
        {
            Layers = new List<HeroLayer> { new() { Z = 3, Img = "images/1.png", Off = new HeroVec(-12.5f, 42) } },
            Hurtboxes = new List<HeroBox> { new(1, -80, 22, 31), new(2, -35, 18, 20) },
        };
        var target = new HeroFrame
        {
            Root = new HeroVec(7, 9), HurtDamage = 500,
            Layers = new List<HeroLayer> { new() { Z = 3, Img = "images/2.png" }, new() { Z = 8, Img = "images/other.png" } },
            Hurtboxes = new List<HeroBox> { new(9, 8, 7, 6) },
            Fx = new HeroFx { Sounds = new List<string> { "sound.ogg" } },
        };
        var oldBoxes = target.Hurtboxes;
        EditorFrameOperations.ApplyLayers(source.Layers, target, true);
        Check(target.Layers[0].Off.X == -12.5f && target.Layers[0].Off.Y == 42
            && target.Layers[0].Img == "images/2.png" && target.Layers.Count == 2 && target.Layers[1].Off.X == 0,
            "editor offsets-only preserves images and unmatched layers, matches by Z");
        EditorFrameOperations.ApplyLayers(source.Layers, target, false);
        Check(target.Layers.Count == 1 && target.Layers[0].Img == "images/1.png"
            && target.Hurtboxes == oldBoxes && target.Root.X == 7 && target.Root.Y == 9
            && target.HurtDamage == 500 && target.Fx.Sounds[0] == "sound.ogg",
            "editor layer replacement leaves hurtboxes/root/FX/damage intact");
        target.Layers[0].Off.X = 99;
        Check(source.Layers[0].Off.X == -12.5f, "editor layers and offsets are deep copies");
        var oldLayers = target.Layers;
        EditorFrameOperations.ApplyHurtboxes(source.Hurtboxes, target);
        Check(target.Hurtboxes.Count == 2 && target.Hurtboxes[0].Cy == -80 && target.Hurtboxes[0].Hw == 22
            && target.Hurtboxes[0].Hh == 31 && target.Layers == oldLayers && target.Root.X == 7
            && target.HurtDamage == 500 && target.Fx.Sounds.Count == 1,
            "editor hurtbox replacement preserves all four parameters and other channels");
        var next = new HeroFrame();
        var snapshot = EditorFrameOperations.CloneBoxes(source.Hurtboxes);
        EditorFrameOperations.ApplyHurtboxes(snapshot, next);
        EditorFrameOperations.ApplyHurtboxes(snapshot, target);
        next.Hurtboxes[0].Cx = 100;
        source.Hurtboxes[0].Cx = 200;
        Check(target.Hurtboxes[0].Cx == 1 && snapshot[0].Cx == 1, "editor clipboard and batch targets are independent snapshots");
        EditorFrameOperations.ApplyHurtboxes(new List<HeroBox>(), target);
        Check(target.Hurtboxes.Count == 0 && target.Layers == oldLayers, "editor empty hurtbox copy restores base-box fallback only");
        EditorFrameOperations.ApplyLayers(new List<HeroLayer>(), target, false);
        Check(target.Layers.Count == 0, "editor empty layer copy clears only layers");
        var self = source.Layers;
        EditorFrameOperations.ApplyLayers(self, source, false);
        Check(source.Layers.Count == 1 && source.Layers[0].Off.X == -12.5f, "editor self-copy is safe");
        var names = new[] { "frame10.png", "frame2.png", "frame1.png", "frame0003.png" };
        var sorted = names.OrderBy(name => name, Comparer<string>.Create(EditorFrameOperations.CompareImagePaths)).ToArray();
        Check(sorted.SequenceEqual(new[] { "frame1.png", "frame2.png", "frame0003.png", "frame10.png" }),
            "editor PNG sequence import uses natural numeric order");
        Check(EditorFrameOperations.CompareImagePaths("frame999999999999999999999999.png", "frame10.png") > 0,
            "editor natural filename order handles arbitrarily long numbers");
    }
}
