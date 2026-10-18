using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MouseKombat.Sim;

internal static class EditorFrameOperations
{
    public static string NextImagePath(string character, string action, IEnumerable<string> reservedPaths)
    {
        static string Segment(string value)
        {
            string safe = new string((value ?? "").Take(80)
                .Select(character => char.IsLetterOrDigit(character) || character == '-' ? character : '_').ToArray()).Trim('_');
            return safe.Length > 0 ? safe : "unnamed";
        }
        string prefix = $"images/{Segment(character)}_{Segment(action)}_";
        var reserved = new HashSet<string>(reservedPaths.Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        int index = 0;
        while (reserved.Contains($"{prefix}{index:D4}.png")) index++;
        return $"{prefix}{index:D4}.png";
    }

    public static int[] FrameMoveOrder(int count, IEnumerable<int> selection, int insertionIndex)
    {
        var original = Enumerable.Range(0, count).ToArray();
        if (insertionIndex < 0 || insertionIndex > count) return original;
        var selected = new HashSet<int>(selection.Where(index => index >= 0 && index < count));
        var remaining = original.Where(index => !selected.Contains(index)).ToArray();
        int destination = insertionIndex - selected.Count(index => index < insertionIndex);
        return remaining.Take(destination).Concat(original.Where(selected.Contains)).Concat(remaining.Skip(destination)).ToArray();
    }

    public static void InsertBlankFrame(HeroActionDef action, int index)
    {
        if (index < 0 || index > action.Frames.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var root = action.Frames.Count == 0 ? null : action.Frames[Math.Max(0, index - 1)].Root;
        action.Frames.Insert(index, new HeroFrame { Root = new HeroVec(root?.X ?? 0, root?.Y ?? 0) });
        static void ShiftRange(int[] range, int insertion)
        {
            if (range == null) return;
            for (int endpoint = 0; endpoint < range.Length; endpoint++)
                if (range[endpoint] >= insertion) range[endpoint]++;
        }
        if (action.CanActNextActionAt >= index) action.CanActNextActionAt++;
        if (action.Attack != null)
        {
            ShiftRange(action.Attack.StartupRange, index);
            ShiftRange(action.Attack.RecoveryRange, index);
            foreach (var active in action.Attack.Actives) ShiftRange(active.ActiveRange, index);
            foreach (var spawn in action.Attack.Projectiles)
                if (spawn.SpawnFrame >= index) spawn.SpawnFrame++;
        }
        if (action.Throw != null)
        {
            foreach (var bind in action.Throw.VictimBind)
                if (bind.Frame >= index) bind.Frame++;
            foreach (var tick in action.Throw.HurtTimeline)
                if (tick.Frame >= index) tick.Frame++;
        }
    }

    public static int CompareImagePaths(string left, string right)
    {
        string leftName = Path.GetFileName(left), rightName = Path.GetFileName(right);
        int leftIndex = 0, rightIndex = 0;
        while (leftIndex < leftName.Length && rightIndex < rightName.Length)
        {
            int compared;
            if (char.IsAsciiDigit(leftName[leftIndex]) && char.IsAsciiDigit(rightName[rightIndex]))
            {
                int leftStart = leftIndex, rightStart = rightIndex;
                while (leftIndex < leftName.Length && char.IsAsciiDigit(leftName[leftIndex])) leftIndex++;
                while (rightIndex < rightName.Length && char.IsAsciiDigit(rightName[rightIndex])) rightIndex++;
                string leftNumber = leftName[leftStart..leftIndex].TrimStart('0');
                string rightNumber = rightName[rightStart..rightIndex].TrimStart('0');
                compared = leftNumber.Length.CompareTo(rightNumber.Length);
                if (compared == 0) compared = string.CompareOrdinal(leftNumber, rightNumber);
            }
            else
            {
                compared = char.ToUpperInvariant(leftName[leftIndex]).CompareTo(char.ToUpperInvariant(rightName[rightIndex]));
                leftIndex++;
                rightIndex++;
            }
            if (compared != 0) return compared;
        }
        int remaining = (leftName.Length - leftIndex).CompareTo(rightName.Length - rightIndex);
        return remaining != 0 ? remaining : string.Compare(left, right, StringComparison.Ordinal);
    }

    public static List<HeroLayer> CloneLayers(IEnumerable<HeroLayer> layers) => layers.Select(layer =>
        new HeroLayer { Z = layer.Z, Img = layer.Img, Off = new HeroVec(layer.Off?.X ?? 0, layer.Off?.Y ?? 0) }).ToList();

    public static List<HeroBox> CloneBoxes(IEnumerable<HeroBox> boxes) => boxes.Select(box =>
        new HeroBox(box.Cx, box.Cy, box.Hw, box.Hh)).ToList();

    public static void ApplyLayers(IEnumerable<HeroLayer> source, HeroFrame target, bool offsetsOnly)
    {
        var snapshot = CloneLayers(source);
        if (!offsetsOnly) target.Layers = snapshot;
        else
            foreach (var layer in target.Layers)
            {
                var match = snapshot.FirstOrDefault(other => other.Z == layer.Z);
                if (match != null) layer.Off = new HeroVec(match.Off.X, match.Off.Y);
            }
    }

    public static void ApplyHurtboxes(IEnumerable<HeroBox> source, HeroFrame target) =>
        target.Hurtboxes = CloneBoxes(source);
}
