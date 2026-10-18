using Godot;
using MouseKombat.Sim;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public partial class SmokeTests
{
    private async Task SettleEditor()
    {
        for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task ImageImportChecks(string root, EditorProject project, EditorTabs tabs, EditorCanvas canvas, MKEditorScreen screen)
    {
        var character = project.Current;
        var first = new HeroActionDef { Name = "IMPORT_A", Frames = new List<HeroFrame> { new() } };
        var second = new HeroActionDef { Name = "IMPORT_B", Frames = new List<HeroFrame> { new() } };
        character.Def.Actions.AddRange(new[] { first, second });
        string redDir = Path.Combine(root, "red"), blueDir = Path.Combine(root, "blue");
        Directory.CreateDirectory(redDir);
        Directory.CreateDirectory(blueDir);
        string redPath = Path.Combine(redDir, "frame0001.png"), bluePath = Path.Combine(blueDir, "frame0001.png");
        var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        image.SavePng(redPath);
        image.Fill(Colors.Blue);
        image.SavePng(bluePath);
        project.SelectedAction = first.Name;
        project.SelectedFrame = 0;
        project.MultiSelect.Clear();
        tabs.Tabs.CurrentTab = 1;
        Invoke(screen, "OnStructureChanged");
        await SettleEditor();
        tabs.DispatchFilesDropped(new[] { redPath }, canvas.GetGlobalRect().GetCenter());
        string firstAsset = first.Frames[0].Layers[0].Img;
        project.SelectedAction = second.Name;
        Invoke(screen, "OnStructureChanged");
        await SettleEditor();
        tabs.DispatchFilesDropped(new[] { bluePath }, canvas.GetGlobalRect().GetCenter());
        string secondAsset = second.Frames[0].Layers[0].Img;
        Check(firstAsset == "images/Test_IMPORT_A_0000.png" && secondAsset == "images/Test_IMPORT_B_0000.png"
            && Image.LoadFromFile(Path.Combine(character.Dir, firstAsset)).GetPixel(0, 0) == Colors.Red
            && Image.LoadFromFile(Path.Combine(character.Dir, secondAsset)).GetPixel(0, 0) == Colors.Blue,
            "same source filenames in different actions produce isolated character/action-scoped image assets");
        first.Frames.Add(new HeroFrame { Layers = EditorFrameOperations.CloneLayers(first.Frames[0].Layers) });
        second.Frames[0].Layers[0].Img = firstAsset;
        project.SelectedAction = first.Name;
        Invoke(screen, "OnStructureChanged");
        await SettleEditor();
        int before = project.UndoCount;
        var card = Field<VBoxContainer>(tabs, "_layerPage").GetChildren().OfType<PanelContainer>().First();
        tabs.DispatchFilesDropped(new[] { bluePath }, card.GetGlobalRect().GetCenter());
        string replacement = first.Frames[0].Layers[0].Img;
        Check(replacement == "images/Test_IMPORT_A_0001.png" && first.Frames[1].Layers[0].Img == firstAsset
            && second.Frames[0].Layers[0].Img == firstAsset && project.UndoCount == before + 1
            && Image.LoadFromFile(Path.Combine(character.Dir, firstAsset)).GetPixel(0, 0) == Colors.Red
            && Image.LoadFromFile(Path.Combine(character.Dir, replacement)).GetPixel(0, 0) == Colors.Blue,
            "single-PNG layer-card replacement changes only that layer, never shared file bytes");
        screen._UnhandledKeyInput(KeyPress(Key.Z));
        character = project.Current;
        Check(character.Action(first.Name).Frames[0].Layers[0].Img == firstAsset
            && Image.LoadFromFile(Path.Combine(character.Dir, firstAsset)).GetPixel(0, 0) == Colors.Red,
            "image replacement undo restores the original reference and original pixels");
        screen._UnhandledKeyInput(KeyPress(Key.Y));
        character = project.Current;
        Check(character.Action(first.Name).Frames[0].Layers[0].Img == replacement, "image replacement redo restores the new asset reference");
        string orphan = Path.Combine(character.Dir, "images", "Test_IMPORT_A_0002.png");
        image.SavePng(orphan);
        var missingReference = character.Action(second.Name).Frames[0].Layers[0];
        missingReference.Img = "images/Test_IMPORT_A_0003.png";
        var allocated = character.ImportImage(redPath, first.Name);
        Check(allocated.Result == ImportResult.Ok && allocated.Path == "images/Test_IMPORT_A_0004.png",
            "allocator reserves unreferenced disk assets and missing-file references without persisting a counter");
        character.Action(first.Name).Name = "RENAMED";
        var renamed = character.ImportImage(bluePath, "RENAMED");
        Check(renamed.Path == "images/Test_RENAMED_0000.png" && File.Exists(Path.Combine(character.Dir, firstAsset))
            && character.Action("RENAMED").Frames[0].Layers[0].Img == replacement,
            "renaming an action only changes the naming scope of future imports");
        Check(Image.LoadFromFile(redPath).GetPixel(0, 0) == Colors.Red && Image.LoadFromFile(bluePath).GetPixel(0, 0) == Colors.Blue,
            "importing images never modifies the user's source PNGs");
    }

    private static Vector2 FramePoint(EditorTimeline timeline, int index, float fraction = 0.5f)
    {
        var cell = (Rect2)typeof(EditorTimeline).GetMethod("CellRect", PrivateInstance).Invoke(timeline, new object[] { index });
        return cell.Position + new Vector2(cell.Size.X * fraction, cell.Size.Y * 0.5f);
    }

    private static void TimelineMouse(EditorTimeline timeline, MouseButton button, bool pressed, Vector2 position) =>
        timeline._GuiInput(new InputEventMouseButton { ButtonIndex = button, Pressed = pressed, Position = position });

    private static void TimelineDrag(EditorTimeline timeline, int source, int target, float fraction)
    {
        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, source));
        var destination = FramePoint(timeline, target, fraction);
        timeline._GuiInput(new InputEventMouseMotion { Position = destination, ButtonMask = MouseButtonMask.Right });
        TimelineMouse(timeline, MouseButton.Right, false, destination);
    }

    private async Task TimelineChecks(EditorProject project, EditorTimeline timeline, EditorTabs tabs, MKEditorScreen screen)
    {
        var action = new HeroActionDef { Name = "TIMELINE", IsAttack = true };
        for (int index = 0; index < 8; index++)
            action.Frames.Add(new HeroFrame
            {
                HurtDamage = index, Root = new HeroVec(index * 2, -4),
                Hurtboxes = new List<HeroBox> { new(index, -20, 10, 15) },
            });
        action.Attack.StartupRange = new[] { 0, 2 };
        action.Attack.RecoveryRange = new[] { 6, 7 };
        action.Attack.Actives.Add(new HeroActive { ActiveRange = new[] { 3, 5 } });
        action.Attack.Projectiles.Add(new HeroProjectileSpawn { SpawnFrame = 4 });
        project.Current.Def.Actions.Add(action);
        project.SelectedAction = action.Name;
        project.SelectedFrame = 0;
        project.MultiSelect.Clear();
        timeline.CellWidth = 60;
        SetField(timeline, "_scroll", 0f);
        SetField(timeline, "_scrollTarget", 0f);
        Invoke(screen, "OnStructureChanged");
        await SettleEditor();
        TimelineMouse(timeline, MouseButton.Left, true, FramePoint(timeline, 1));
        timeline._GuiInput(new InputEventMouseMotion { Position = FramePoint(timeline, 2), ButtonMask = MouseButtonMask.Left });
        TimelineMouse(timeline, MouseButton.Left, false, FramePoint(timeline, 2));
        Check(project.MultiSelect.SetEquals(new[] { 1, 2 }), "left-button brushing selects a frame interval");
        var original = action.Frames.ToArray();
        int before = project.UndoCount;
        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, 2));
        Check(!timeline.GetChildren().OfType<PopupMenu>().Any(menu => menu.Visible), "right-button down defers the context menu until release");
        var destination = FramePoint(timeline, 6, 0.25f);
        timeline._GuiInput(new InputEventMouseMotion { Position = destination, ButtonMask = MouseButtonMask.Right });
        Check(Field<int>(timeline, "_rightInsertion") == 6, "right-drag target left half previews the insertion boundary");
        TimelineMouse(timeline, MouseButton.Right, false, destination);
        Check(action.Frames.Select(frame => frame.HurtDamage).SequenceEqual(new[] { 0, 3, 4, 5, 1, 2, 6, 7 })
            && project.MultiSelect.SetEquals(new[] { 4, 5 }) && project.SelectedFrame == 5 && project.UndoCount == before + 1,
            "right-drag moves the whole selection before target, preserves order and selection, and creates one undo");
        Check(action.Frames[4] == original[1] && action.Frames[4].Root.X == 2 && action.Frames[4].Hurtboxes[0].Cx == 1
            && action.Attack.Actives[0].ActiveRange.SequenceEqual(new[] { 3, 5 }) && action.Attack.Projectiles[0].SpawnFrame == 4,
            "reorder preserves complete frame objects and image paths while action-level timing remains at numbered slots");
        screen._UnhandledKeyInput(KeyPress(Key.Z));
        action = project.Current.Action("TIMELINE");
        Check(action.Frames.Select(frame => frame.HurtDamage).SequenceEqual(Enumerable.Range(0, 8)), "block-move undo restores the original order");
        screen._UnhandledKeyInput(KeyPress(Key.Y));
        action = project.Current.Action("TIMELINE");
        Check(action.Frames.Select(frame => frame.HurtDamage).SequenceEqual(new[] { 0, 3, 4, 5, 1, 2, 6, 7 }), "block-move redo restores reordered frames");
        project.MultiSelect.Clear();
        project.MultiSelect.UnionWith(new[] { 4, 5 });
        project.SelectedFrame = 5;
        TimelineDrag(timeline, 5, 0, 0.75f);
        Check(action.Frames.Select(frame => frame.HurtDamage).SequenceEqual(Enumerable.Range(0, 8))
            && project.MultiSelect.SetEquals(new[] { 1, 2 }), "right-drag target right half moves the block after target, including backwards moves");
        before = project.UndoCount;
        TimelineDrag(timeline, 2, 1, 0.25f);
        Check(project.UndoCount == before && action.Frames.Count == 8, "dropping on another selected frame is a no-op");
        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, 2));
        TimelineMouse(timeline, MouseButton.Right, false, new Vector2(-50, -50));
        Check(project.UndoCount == before && !Field<bool>(timeline, "_rightPressed"), "dropping outside the frame strip cancels without edits");
        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, 2));
        timeline._GuiInput(new InputEventMouseMotion { Position = FramePoint(timeline, 6), ButtonMask = MouseButtonMask.Right });
        screen._UnhandledKeyInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        TimelineMouse(timeline, MouseButton.Right, false, FramePoint(timeline, 6));
        Check(project.UndoCount == before && !Field<bool>(timeline, "_rightPressed"), "Escape cancels a pending frame move without opening the exit dialog");

        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, 2, 0.25f));
        timeline._GuiInput(new InputEventMouseMotion { Position = FramePoint(timeline, 2, 0.75f), ButtonMask = MouseButtonMask.Right });
        TimelineMouse(timeline, MouseButton.Right, false, FramePoint(timeline, 2, 0.75f));
        var menu = timeline.GetChildren().OfType<PopupMenu>().Last();
        Check(menu.Visible && menu.GetItemIndex(2) >= 0 && menu.GetItemIndex(3) >= 0 && project.MultiSelect.SetEquals(new[] { 1, 2 }),
            "right-button release in the same cell opens the old menu plus left/right insert options, preserving the selection");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        menu.Hide();
        Check(action.Frames.Count == 9 && project.SelectedFrame == 2 && project.MultiSelect.SetEquals(new[] { 2 })
            && action.Frames[2].Layers.Count == 0 && action.Frames[2].Hurtboxes.Count == 0 && action.Frames[2].Root.X == 2
            && action.Attack.Projectiles[0].SpawnFrame == 5 && action.Attack.Actives[0].ActiveRange.SequenceEqual(new[] { 4, 6 }),
            "left-insert menu inserts a blank frame, inherits root, and shifts later timing/event indices");
        screen._UnhandledKeyInput(KeyPress(Key.Z));
        action = project.Current.Action("TIMELINE");
        Check(action.Frames.Count == 8 && action.Attack.Projectiles[0].SpawnFrame == 4
            && action.Attack.Actives[0].ActiveRange.SequenceEqual(new[] { 3, 5 }), "insert undo restores frames and all action-level timing");
        await SettleEditor();
        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, 7));
        TimelineMouse(timeline, MouseButton.Right, false, FramePoint(timeline, 7));
        menu = timeline.GetChildren().OfType<PopupMenu>().Last();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
        menu.Hide();
        Check(action.Frames.Count == 9 && project.SelectedFrame == 8 && action.Frames[8].Layers.Count == 0, "right-insert menu works after the final frame");
        screen._UnhandledKeyInput(KeyPress(Key.Z));
        action = project.Current.Action("TIMELINE");
        Check(action.Frames.Count == 8 && project.SelectedFrame == 7, "undoing an insertion at the end clamps selection to a valid existing frame");
        project.MultiSelect.Clear();
        project.MultiSelect.UnionWith(new[] { 1, 2 });
        project.SelectedFrame = 2;
        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, 2));
        var end = new Vector2(action.Frames.Count * (timeline.CellWidth + EditorTimeline.CellGap) + 8, 30);
        timeline._GuiInput(new InputEventMouseMotion { Position = end, ButtonMask = MouseButtonMask.Right });
        TimelineMouse(timeline, MouseButton.Right, false, end);
        Check(action.Frames.Select(frame => frame.HurtDamage).SequenceEqual(new[] { 0, 3, 4, 5, 6, 7, 1, 2 })
            && project.MultiSelect.SetEquals(new[] { 6, 7 }), "right-drag to the trailing plus cell moves a selected interval to the end");
        for (int index = 8; index < 40; index++) action.Frames.Add(new HeroFrame { HurtDamage = index });
        SetField(timeline, "_scroll", 0f);
        SetField(timeline, "_scrollTarget", 0f);
        int edgeCell = (int)((timeline.Size.X - 8) / (timeline.CellWidth + EditorTimeline.CellGap));
        var edge = new Vector2(timeline.Size.X - 8, 40);
        TimelineMouse(timeline, MouseButton.Right, true, edge);
        timeline._Process(0.2);
        Check(Field<float>(timeline, "_scrollTarget") == 0, "holding a stationary right click near the edge does not accidentally scroll or move frames");
        TimelineMouse(timeline, MouseButton.Right, false, edge);
        menu = timeline.GetChildren().OfType<PopupMenu>().Last();
        menu.Hide();
        project.MultiSelect.Clear();
        project.MultiSelect.UnionWith(new[] { 0, 1 });
        project.SelectedFrame = 1;
        TimelineMouse(timeline, MouseButton.Right, true, FramePoint(timeline, 1));
        timeline._GuiInput(new InputEventMouseMotion { Position = edge, ButtonMask = MouseButtonMask.Right });
        timeline._Process(0.1);
        Check(edgeCell >= 2 && Field<float>(timeline, "_scrollTarget") > 0 && Field<int>(timeline, "_rightInsertion") >= 0,
            "genuine right-button dragging at the edge auto-scrolls and maintains a valid insertion preview");
        timeline.CancelFrameDrag();
    }
}
