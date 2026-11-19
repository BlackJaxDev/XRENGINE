namespace UnityEngine;

/// <summary>Stores component type requirements for later object-store validation.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequireComponent : Attribute
{
    public Type m_Type0 { get; }
    public Type? m_Type1 { get; }
    public Type? m_Type2 { get; }

    /// <summary>Stores one required component type.</summary>
    public RequireComponent(Type type0) => m_Type0 = type0;

    /// <summary>Stores two required component types.</summary>
    public RequireComponent(Type type0, Type type1)
    {
        m_Type0 = type0;
        m_Type1 = type1;
    }

    /// <summary>Stores three required component types.</summary>
    public RequireComponent(Type type0, Type type1, Type type2)
    {
        m_Type0 = type0;
        m_Type1 = type1;
        m_Type2 = type2;
    }
}
