using Godot;
using MouseKombat.Sim;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

public partial class SmokeTests : Node
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private int _checks;

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance).GetValue(target);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, PrivateInstance).Invoke(target, null);

    private void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
        GD.Print("PASS editor: " + label);
    }

    private static InputEventKey KeyPress(Key key, bool shift = false) => new()
    {
        Keycode = key, Pressed = true, CtrlPressed = true, ShiftPressed = shift,
    };

    public override async void _Ready()
    {
        try
        {
            var screen = GD.Load<PackedScene>("res://MKEditor.tscn").Instantiate<MKEditorScreen>();
            AddChild(screen);
            GetWindow().Size = new Vector2I(1440, 900);
            for (int frame = 0; frame < 4; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var tabs = Field<EditorTabs>(screen, "_tabs");
            var canvas = Field<EditorCanvas>(screen, "_canvas");
            var timeline = Field<EditorTimeline>(screen, "_timeline");
            var timelineBox = Field<VBoxContainer>(screen, "_timelineBox");
            SetField(screen, "_optionsWidth", 440f);
            SetField(screen, "_timelineHeight", 180f);
            Invoke(screen, "ApplyEditorLayout");
            for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(Math.Abs(tabs.Size.X - 440) < 2 && Math.Abs(timelineBox.Size.Y - 180) < 2,
                $"default layout sizes (options={tabs.Size.X}, timeline={timelineBox.Size.Y})");
            GetWindow().Size = new Vector2I(1280, 800);
            for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(Math.Abs(tabs.Size.X - 440) < 2 && Math.Abs(timelineBox.Size.Y - 180) < 2, "resize preserves panel sizes");

            string root = Path.Combine(Path.GetTempPath(), "mouse-ftg-editor-tests-" + Guid.NewGuid().ToString("N"));
            var project = new EditorProject { SelectedChar = "Test", SelectedAction = "IDLE", SelectedFrame = 1 };
            typeof(EditorProject).GetProperty(nameof(EditorProject.HeroesRoot)).SetValue(project, root);
            var character = EditorChar.CreateNew(root, "Test", "Editor Test");
            project.Chars.Add(character);
            var action = character.Action("IDLE");
            action.Frames[0].Layers.Add(new HeroLayer { Img = "", Off = new HeroVec(13, -27), Z = 2 });
            action.Frames[0].Hurtboxes.Add(new HeroBox(2, -50, 20, 30));
            action.Frames.Add(new HeroFrame { Root = new HeroVec(7, 8), HurtDamage = 123 });
            action.Frames[1].Hurtboxes.Add(new HeroBox(9, -30, 10, 15));
            SetField(screen, "_project", project);
            tabs.Project = canvas.Project = timeline.Project = project;
            Invoke(screen, "OnStructureChanged");
            tabs.Tabs.CurrentTab = 1;
            for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!project.Dirty, "page rebuilds and selection do not dirty project");

            string single = Path.Combine(root, "single.png");
            var image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
            image.Fill(Colors.White);
            image.SavePng(single);
            tabs.DispatchFilesDropped(new[] { single }, canvas.GetGlobalRect().GetCenter());
            string singleAsset = action.Frames[1].Layers[0].Img;
            Check(action.Frames[1].Layers.Count == 1 && action.Frames[1].Layers[0].Off.Y == -27
                && action.Frames[1].Hurtboxes[0].Cx == 9 && project.UndoCount == 1,
                "PNG dropped on empty canvas creates a layer, inherits offset, keeps hurtboxes, one undo");
            var sequence = new[] { "frame10.png", "frame2.png", "frame1.png" }.Select(name => Path.Combine(root, name)).ToArray();
            foreach (string path in sequence)
            {
                image.Fill(path.EndsWith("frame1.png") ? Colors.Blue : path.EndsWith("frame2.png") ? Colors.Green : Colors.Red);
                image.SavePng(path);
            }
            tabs.DispatchFilesDropped(sequence, canvas.GetGlobalRect().GetCenter());
            var sequenceAssets = action.Frames.Skip(1).Select(frame => frame.Layers[0].Img).ToArray();
            Check(action.Frames.Count == 4 && Image.LoadFromFile(Path.Combine(character.Dir, sequenceAssets[0])).GetPixel(0, 0) == Colors.Blue
                && Image.LoadFromFile(Path.Combine(character.Dir, sequenceAssets[1])).GetPixel(0, 0) == Colors.Green
                && Image.LoadFromFile(Path.Combine(character.Dir, sequenceAssets[2])).GetPixel(0, 0) == Colors.Red
                && action.Frames[3].Layers[0].Off.X == 13 && action.Frames[3].Root.X == 7 && project.UndoCount == 2,
                "sequence drop uses natural order, extends timeline, carries offset, one undo");
            screen._UnhandledKeyInput(KeyPress(Key.Z));
            action = project.Current.Action("IDLE");
            Check(action.Frames.Count == 2 && action.Frames[1].Layers[0].Img == singleAsset, "sequence undo restores full frame count and image");
            screen._UnhandledKeyInput(KeyPress(Key.Y));
            action = project.Current.Action("IDLE");
            Check(action.Frames.Count == 4 && action.Frames[3].Layers[0].Img == sequenceAssets[2], "sequence redo restores imported frames");

            project.SelectedFrame = 1;
            tabs.Tabs.CurrentTab = 1;
            tabs.HandleFrameShortcut(KeyPress(Key.V, true));
            Check(action.Frames[1].Layers[0].Img == "" && action.Frames[1].Hurtboxes[0].Cx == 9
                && action.Frames[1].Root.X == 7 && action.Frames[1].HurtDamage == 123,
                "layer previous-frame shortcut does not copy hurtboxes/root/damage");
            tabs.Tabs.CurrentTab = 5;
            tabs.HandleFrameShortcut(KeyPress(Key.V, true));
            Check(action.Frames[1].Hurtboxes[0].Cx == 2 && action.Frames[1].Hurtboxes[0].Hh == 30
                && action.Frames[1].Layers[0].Img == "" && action.Frames[1].Root.X == 7,
                "hurtbox previous-frame shortcut copies full boxes but leaves animation/root intact");
            project.SelectedFrame = 0;
            tabs.HandleFrameShortcut(KeyPress(Key.C));
            action.Frames[0].Hurtboxes[0].Cx = 88;
            project.SelectedFrame = 2;
            tabs.HandleFrameShortcut(KeyPress(Key.V));
            Check(action.Frames[2].Hurtboxes[0].Cx == 2 && action.Frames[2].Layers[0].Img == sequenceAssets[1], "hurt clipboard is a snapshot independent from animation clipboard");
            project.MultiSelect.Clear();
            project.MultiSelect.UnionWith(new[] { 1, 2, 3 });
            int before = project.UndoCount;
            typeof(EditorTabs).GetMethod("PasteFrameChannel", PrivateInstance).Invoke(tabs, new object[] { true, true });
            Check(project.UndoCount == before + 1 && action.Frames.Skip(1).All(frame => frame.Hurtboxes[0].Cx == 2), "batch hurtbox paste creates only one undo entry");
            action.Frames[1].Hurtboxes[0].Cx = 55;
            Check(action.Frames[2].Hurtboxes[0].Cx == 2 && action.Frames[3].Hurtboxes[0].Cx == 2, "batch hurtbox destinations do not share references");
            project.SelectedFrame = 0;
            before = project.UndoCount;
            tabs.HandleFrameShortcut(KeyPress(Key.V, true));
            Check(project.UndoCount == before, "previous-frame operation on frame zero is a no-op");
            action.Frames.Add(new HeroFrame());
            project.SelectedFrame = action.Frames.Count - 1;
            tabs.Tabs.CurrentTab = 1;
            Invoke(screen, "OnStructureChanged");
            for (int frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            tabs.DispatchFilesDropped(new[] { single }, tabs.GetGlobalRect().GetCenter());
            Check(action.Frames[^1].Layers.Count == 1 && action.Frames[^1].Layers[0].Off.X == 13,
                "PNG dropped on empty layer-page space also creates a layer");
            project.SelectedFrame = 0;
            var button = new Button { Text = "Focus test" };
            screen.AddChild(button);
            button.GrabFocus();
            screen._Input(new InputEventKey { Keycode = Key.Right, Pressed = true });
            Check(project.SelectedFrame == 1, "arrow keys step frames even when a button has focus");
            var text = new LineEdit();
            screen.AddChild(text);
            text.GrabFocus();
            before = project.UndoCount;
            screen._Input(KeyPress(Key.V, true));
            screen._Input(new InputEventKey { Keycode = Key.Right, Pressed = true });
            Check(project.UndoCount == before && project.SelectedFrame == 1, "frame shortcuts do not hijack text-field editing");
            text.ReleaseFocus();
            await ImageImportChecks(root, project, tabs, canvas, screen);
            await TimelineChecks(project, timeline, tabs, screen);
            GD.Print($"ALL {_checks} EDITOR SMOKE CHECKS PASS");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }
}
