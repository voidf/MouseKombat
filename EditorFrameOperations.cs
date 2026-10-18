using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MouseKombat.Sim;

internal static class EditorFrameOperations
{
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
