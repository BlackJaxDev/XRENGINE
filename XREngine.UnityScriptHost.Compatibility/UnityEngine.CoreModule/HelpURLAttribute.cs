namespace UnityEngine;

/// <summary>Stores a help URL as authoring metadata.</summary>
[AttributeUsage(AttributeTargets.Class)]
public class HelpURLAttribute : Attribute
{
    public string URL { get; }

    /// <summary>Stores the supplied help URL without opening it.</summary>
    public HelpURLAttribute(string url) => URL = url;
}
