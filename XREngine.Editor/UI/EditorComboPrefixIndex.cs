using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Finds a label prefix without scanning every option on each key press.</summary>
internal sealed class EditorComboPrefixIndex
{
    private readonly IReadOnlyList<string> _labels;
    private readonly Dictionary<string, List<int>> _prefixes = new(StringComparer.OrdinalIgnoreCase);

    public EditorComboPrefixIndex(IReadOnlyList<string> labels)
    {
        _labels = labels;
        Build();
    }

    public int Find(string prefix, int startIndex)
    {
        if (!_prefixes.TryGetValue(prefix, out List<int>? matches) || matches.Count == 0)
            return -1;
        int first = matches.BinarySearch(startIndex);
        if (first < 0)
            first = ~first;
        return matches[first < matches.Count ? first : 0];
    }

    private void Build()
    {
        for (int index = 0; index < _labels.Count; index++)
        {
            string label = _labels[index];
            for (int length = 1; length <= Math.Min(label.Length, UIComboBoxComponent.MaximumTypeAheadLength); length++)
            {
                string prefix = label[..length];
                if (!_prefixes.TryGetValue(prefix, out List<int>? matches))
                    _prefixes.Add(prefix, matches = []);
                matches.Add(index);
            }
        }
    }
}
