namespace UnityEditor;

/// <summary>Stores editor menu metadata. It does not register a menu or invoke a method.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MenuItem : Attribute
{
    public string menuItem { get; }
    public bool validate { get; }
    public int priority { get; }

    public MenuItem(string itemName, bool isValidateFunction = false, int priority = 1000)
    {
        menuItem = itemName;
        validate = isValidateFunction;
        this.priority = priority;
    }
}
