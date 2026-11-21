namespace UnityEngine;

/// <summary>Stores a requested Unity startup point without scheduling or invoking the method.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RuntimeInitializeOnLoadMethodAttribute : Scripting.PreserveAttribute
{
    /// <summary>Records the default startup point, which follows scene loading.</summary>
    public RuntimeInitializeOnLoadMethodAttribute()
        : this(RuntimeInitializeLoadType.AfterSceneLoad)
    {
    }

    /// <summary>Records the requested startup point without running the marked method.</summary>
    public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType)
    {
        this.loadType = loadType;
    }

    public RuntimeInitializeLoadType loadType { get; }
}
