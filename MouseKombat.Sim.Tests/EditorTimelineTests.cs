using System;
using System.Collections.Generic;
using System.Linq;
using MouseKombat.Sim;

internal static partial class Program
{
    private static void EditorTimelineTests()
    {
        Check(EditorFrameOperations.FrameMoveOrder(8, new[] { 1, 2 }, 6).SequenceEqual(new[] { 0, 3, 4, 5, 1, 2, 6, 7 }),
            "editor move a selected block before a later frame without replacing anything");
        Check(EditorFrameOperations.FrameMoveOrder(8, new[] { 4, 5 }, 1).SequenceEqual(new[] { 0, 4, 5, 1, 2, 3, 6, 7 }),
            "editor move a selected block backwards after target frame");
        Check(EditorFrameOperations.FrameMoveOrder(5, new[] { 1, 2 }, 5).SequenceEqual(new[] { 0, 3, 4, 1, 2 })
            && EditorFrameOperations.FrameMoveOrder(5, new[] { 3, 4 }, 0).SequenceEqual(new[] { 3, 4, 0, 1, 2 }),
            "editor block moves support the beginning and end of the timeline");
        Check(EditorFrameOperations.FrameMoveOrder(5, new[] { 1, 2 }, 2).SequenceEqual(Enumerable.Range(0, 5))
            && EditorFrameOperations.FrameMoveOrder(5, new[] { 1, 2 }, 3).SequenceEqual(Enumerable.Range(0, 5)),
            "editor drops inside or adjacent to a contiguous selection are no-ops");
        bool allMovesValid = true;
        for (int count = 1; count <= 8; count++)
            for (int mask = 0; mask < 1 << count; mask++)
                for (int insertion = 0; insertion <= count; insertion++)
                {
                    var selected = Enumerable.Range(0, count).Where(index => (mask & (1 << index)) != 0).ToArray();
                    var order = EditorFrameOperations.FrameMoveOrder(count, selected, insertion);
                    allMovesValid &= order.OrderBy(index => index).SequenceEqual(Enumerable.Range(0, count))
                        && order.Where(selected.Contains).SequenceEqual(selected)
                        && order.Where(index => !selected.Contains(index)).SequenceEqual(Enumerable.Range(0, count).Except(selected));
                }
        Check(allMovesValid, "editor exhaustive reorder checks preserve every frame and both relative orders");
        var action = new HeroActionDef { CanActNextActionAt = 3 };
        for (int index = 0; index < 5; index++) action.Frames.Add(new HeroFrame { Root = new HeroVec(index * 2, -3) });
        action.Attack.StartupRange = new[] { 0, 1 };
        action.Attack.RecoveryRange = new[] { 3, 4 };
        action.Attack.Actives.Add(new HeroActive { ActiveRange = new[] { 1, 3 } });
        action.Attack.Projectiles.Add(new HeroProjectileSpawn { SpawnFrame = 2 });
        action.Throw.VictimBind.Add(new HeroBindKey { Frame = 2 });
        action.Throw.HurtTimeline.Add(new HeroHurtTick { Frame = 3 });
        var original = action.Frames.ToArray();
        EditorFrameOperations.InsertBlankFrame(action, 2);
        Check(action.Frames.Count == 6 && action.Frames[2].Layers.Count == 0 && action.Frames[2].Hurtboxes.Count == 0
            && action.Frames[2].Root.X == 2 && action.Frames[2].Root.Y == -3 && action.Frames[3] == original[2],
            "editor inserts an empty frame, carries root, and preserves following frame identities");
        Check(action.Attack.StartupRange.SequenceEqual(new[] { 0, 1 }) && action.Attack.RecoveryRange.SequenceEqual(new[] { 4, 5 })
            && action.Attack.Actives[0].ActiveRange.SequenceEqual(new[] { 1, 4 }),
            "editor insertion shifts later ranges and extends ranges containing the inserted frame");
        Check(action.CanActNextActionAt == 4 && action.Attack.Projectiles[0].SpawnFrame == 3
            && action.Throw.VictimBind[0].Frame == 3 && action.Throw.HurtTimeline[0].Frame == 4,
            "editor insertion shifts action-ready/projectile/bind/damage event indices");
        var empty = new HeroActionDef();
        EditorFrameOperations.InsertBlankFrame(empty, 0);
        Check(empty.Frames.Count == 1 && empty.CanActNextActionAt == -1 && empty.Frames[0].Root.X == 0,
            "editor insertion supports empty actions and preserves disabled ready-frame sentinel");
        var reserved = new List<string> { "images/Hamster_2MP_0000.png", "images/Hamster_2MP_0002.png" };
        Check(EditorFrameOperations.NextImagePath("Hamster", "2MP", reserved) == "images/Hamster_2MP_0001.png",
            "editor image IDs use MEX rather than frame indices or high-water counters");
        reserved.Add("IMAGES\\HAMSTER_2MP_0001.PNG");
        Check(EditorFrameOperations.NextImagePath("Hamster", "2MP", reserved) == "images/Hamster_2MP_0003.png"
            && EditorFrameOperations.NextImagePath("Hamster", "5HP", reserved) == "images/Hamster_5HP_0000.png",
            "editor image allocator is case-insensitive and action-scoped");
        string safe = EditorFrameOperations.NextImagePath("../仓鼠", "../2MP:*?", Array.Empty<string>());
        Check(safe.StartsWith("images/") && safe.Split('/').Length == 2 && !safe.Contains("..")
            && safe.IndexOfAny(new[] { ':', '*', '?' }) < 0,
            "editor generated names cannot escape the image folder");
    }
}
