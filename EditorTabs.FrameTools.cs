using Godot;
using MouseKombat.Sim;
using System;
using System.Collections.Generic;
using System.Linq;

public sealed partial class EditorTabs
{
    private readonly List<(LineEdit widget, Func<double> read)> _liveHurtNums = new();
    private List<HeroLayer> _layerClipboard;
    private List<HeroBox> _hurtClipboard;
    private string _layerClipboardChar, _layerClipboardLabel, _hurtClipboardLabel;
    private bool _offsetsOnly;
    private int _layerSourceFrame, _hurtSourceFrame;

    private void BuildFrameTransferTools(VBoxContainer page, bool hurt)
    {
        var action = Project.Current?.Action(Project.SelectedAction);
        var frame = CurrentFrame();
        if (action == null || frame == null) return;
        Section(page, $"{action.Name} · 第 {Project.SelectedFrame} 帧 · {(hurt ? "受击盒" : "图层")}");
        var row = new HBoxContainer();
        AddToolButton(row, "沿用上一帧", () => ApplyFrameSource(hurt, Project.SelectedFrame - 1, false),
            "Ctrl+Shift+V：覆盖当前帧；只操作本页的数据。第 0 帧不可用。", Project.SelectedFrame == 0);
        AddToolButton(row, "复制", () => CopyFrameChannel(hurt), "Ctrl+C：复制本帧状态到本页的独立剪贴板。");
        AddToolButton(row, "粘贴", () => PasteFrameChannel(hurt, false), "Ctrl+V：用已复制状态覆盖当前帧，可反复粘贴。",
            hurt ? _hurtClipboard == null : _layerClipboard == null || _layerClipboardChar != Project.SelectedChar);
        page.AddChild(row);

        var sourceRow = new HBoxContainer();
        sourceRow.AddChild(new Label { Text = "来源帧" });
        var source = new SpinBox
        {
            MinValue = 0, MaxValue = action.Frames.Count - 1, Step = 1,
            Value = Mathf.Clamp(hurt ? _hurtSourceFrame : _layerSourceFrame, 0, action.Frames.Count - 1),
            CustomMinimumSize = new Vector2(68, 0),
        };
        source.ValueChanged += value => { if (hurt) _hurtSourceFrame = (int)value; else _layerSourceFrame = (int)value; };
        sourceRow.AddChild(source);
        AddToolButton(sourceRow, "覆盖当前帧", () => ApplyFrameSource(hurt, (int)source.Value, false), "从指定帧复制，不必先切换过去。");
        AddToolButton(sourceRow, "覆盖选中帧", () => ApplyFrameSource(hurt, (int)source.Value, true),
            "在时间轴拖选多帧后，统一覆盖；整个操作可一次撤销。");
        page.AddChild(sourceRow);

        var clipRow = new HBoxContainer();
        AddToolButton(clipRow, "粘贴到选中帧", () => PasteFrameChannel(hurt, true), "将本页剪贴板批量覆盖到时间轴选中帧。",
            hurt ? _hurtClipboard == null : _layerClipboard == null || _layerClipboardChar != Project.SelectedChar);
        clipRow.AddChild(new Label
        {
            Text = "剪贴板：" + ((hurt ? _hurtClipboardLabel : _layerClipboardLabel) ?? "空"),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        });
        page.AddChild(clipRow);
        if (!hurt)
            page.AddChild(Check("仅复制偏移（保留各帧图片；按 Z 对应）", _offsetsOnly,
                value => _offsetsOnly = value, "对沿用、指定来源覆盖、粘贴及批量操作均生效；未匹配 Z 的图层保持原样。"));
        page.AddChild(new Label
        {
            Text = hurt ? "仅覆盖受击盒，不改图层、根坐标或打击盒。" : "仅覆盖图层，不改受击盒、根坐标或特效。",
            Modulate = new Color(1, 1, 1, 0.6f),
        });
    }

    private static void AddToolButton(HBoxContainer row, string text, Action pressed, string tip, bool disabled = false)
    {
        var button = new Button { Text = text, TooltipText = tip, Disabled = disabled };
        button.Pressed += pressed;
        row.AddChild(button);
    }

    private int[] FrameTargets(bool selected)
    {
        int count = Project.Current?.Action(Project.SelectedAction)?.Frames.Count ?? 0;
        return (selected && Project.MultiSelect.Count > 0 ? Project.MultiSelect : new HashSet<int> { Project.SelectedFrame })
            .Where(index => index >= 0 && index < count).OrderBy(index => index).ToArray();
    }

    private void CopyFrameChannel(bool hurt)
    {
        var frame = CurrentFrame();
        if (frame == null) return;
        string label = $"{Project.SelectedAction} / {Project.SelectedFrame}";
        if (hurt)
        {
            _hurtClipboard = EditorFrameOperations.CloneBoxes(frame.Hurtboxes);
            _hurtClipboardLabel = label + (frame.Hurtboxes.Count == 0 ? "（基础盒）" : "");
            RebuildHurtPage();
        }
        else
        {
            _layerClipboard = EditorFrameOperations.CloneLayers(frame.Layers);
            _layerClipboardChar = Project.SelectedChar;
            _layerClipboardLabel = label;
            RebuildLayerPage();
        }
    }

    private void ApplyFrameSource(bool hurt, int source, bool selected)
    {
        var action = Project.Current?.Action(Project.SelectedAction);
        if (action == null || source < 0 || source >= action.Frames.Count) return;
        ApplyFrameChannel(hurt, action.Frames[source].Layers, action.Frames[source].Hurtboxes,
            FrameTargets(selected).Where(index => index != source).ToArray());
    }

    private void PasteFrameChannel(bool hurt, bool selected)
    {
        if (hurt ? _hurtClipboard == null : _layerClipboard == null || _layerClipboardChar != Project.SelectedChar) return;
        ApplyFrameChannel(hurt, _layerClipboard, _hurtClipboard, FrameTargets(selected));
    }

    private void ApplyFrameChannel(bool hurt, IEnumerable<HeroLayer> layers, IEnumerable<HeroBox> boxes, int[] targets)
    {
        if (targets.Length == 0) return;
        var action = Project.Current.Action(Project.SelectedAction);
        var layerSnapshot = hurt ? null : EditorFrameOperations.CloneLayers(layers);
        var boxSnapshot = hurt ? EditorFrameOperations.CloneBoxes(boxes) : null;
        Project.PushUndo();
        foreach (int index in targets)
        {
            if (hurt) EditorFrameOperations.ApplyHurtboxes(boxSnapshot, action.Frames[index]);
            else EditorFrameOperations.ApplyLayers(layerSnapshot, action.Frames[index], _offsetsOnly);
        }
        Canvas.SetSelection(new EditorCanvas.Selection());
        if (hurt) RebuildHurtPage(); else RebuildLayerPage();
        Changed?.Invoke();
    }

    public bool HandleFrameShortcut(InputEventKey key)
    {
        if (key.AltPressed || !key.CtrlPressed || (Tabs.CurrentTab != 1 && Tabs.CurrentTab != 5)) return false;
        bool hurt = Tabs.CurrentTab == 5;
        if (key.Keycode == Key.C && !key.ShiftPressed) CopyFrameChannel(hurt);
        else if (key.Keycode == Key.V)
        {
            if (key.ShiftPressed) ApplyFrameSource(hurt, Project.SelectedFrame - 1, false);
            else PasteFrameChannel(hurt, false);
        }
        else return false;
        return true;
    }

    private void RebuildHurtPage()
    {
        ClearChildren(_hurtPage);
        _liveHurtNums.Clear();
        var frame = CurrentFrame();
        if (frame == null)
        {
            _hurtPage.AddChild(new Label { Text = "（先选择一个动作和帧）" });
            return;
        }
        BuildFrameTransferTools(_hurtPage, true);
        BuildHurtboxSection(frame);
    }

    private void ImportFrameImages(string[] files, bool replaceSelected, HeroLayer selected = null)
    {
        var action = Project.Current?.Action(Project.SelectedAction);
        var frame = CurrentFrame();
        if (action == null || frame == null) return;
        var paths = files.Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, Comparer<string>.Create(EditorFrameOperations.CompareImagePaths)).ToArray();
        if (paths.Length == 0) return;
        if (selected == null && replaceSelected && Canvas.Selected.Kind == EditorCanvas.SelectionKind.Layer
            && Canvas.Selected.Index >= 0 && Canvas.Selected.Index < frame.Layers.Count)
            selected = frame.Layers[Canvas.Selected.Index];
        var previous = Project.SelectedFrame > 0 ? action.Frames[Project.SelectedFrame - 1] : null;
        var template = selected ?? previous?.Layers.FirstOrDefault();
        int z = selected?.Z ?? (frame.Layers.Count > 0 ? frame.Layers.Max(layer => layer.Z) + 1 : template?.Z ?? 0);
        float x = template?.Off?.X ?? 0, y = template?.Off?.Y ?? 0;
        int start = Project.SelectedFrame;
        int imported = 0;
        var failures = new List<string>();
        foreach (string path in paths)
        {
            var result = Project.Current.ImportImage(path, overwrite: true);
            if (result.Result != ImportResult.Ok || result.Path == null) { failures.Add(System.IO.Path.GetFileName(path)); continue; }
            if (imported == 0) Project.PushUndo();
            int index = start + imported;
            while (action.Frames.Count <= index)
            {
                var root = action.Frames[^1].Root;
                action.Frames.Add(new HeroFrame { Root = new HeroVec(root?.X ?? 0, root?.Y ?? 0) });
            }
            var target = action.Frames[index];
            var layer = target.Layers.FirstOrDefault(item => item.Z == z);
            if (layer == null)
            {
                layer = new HeroLayer { Z = z, Off = new HeroVec(x, y) };
                target.Layers.Add(layer);
                target.Layers.Sort((left, right) => left.Z.CompareTo(right.Z));
            }
            layer.Img = result.Path;
            Project.Current.InvalidateImage(result.Path);
            imported++;
        }
        if (imported > 0)
        {
            Canvas.SetSelection(new EditorCanvas.Selection
            {
                Kind = EditorCanvas.SelectionKind.Layer,
                Index = frame.Layers.FindIndex(layer => layer.Z == z), ActiveIndex = -1,
            });
            Tabs.CurrentTab = 1;
            RebuildLayerPage();
            RebuildHurtPage();
            Changed?.Invoke();
        }
        if (failures.Count > 0) Warn("以下 PNG 导入失败：\n" + string.Join("\n", failures));
    }
}
