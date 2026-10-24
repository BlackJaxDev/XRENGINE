using System.Collections;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorCollectionToStringTarget
{
    public Action? OnToString { get; set; }
    public IDictionary DictionaryKey { get; }
    public IDictionary DictionaryValue { get; }
    public ICollection GenericValue { get; }

    public EditorCollectionToStringTarget()
    {
        EditorCollectionToStringValue key = new(() =>
        {
            OnToString?.Invoke();
            return "Key";
        });
        EditorCollectionToStringValue value = new(() =>
        {
            OnToString?.Invoke();
            return "Value";
        });
        DictionaryKey = new Hashtable { [key] = 1 };
        DictionaryValue = new Hashtable { ["one"] = value };
        GenericValue = new Queue(new object[] { value });
    }
}
