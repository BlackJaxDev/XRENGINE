namespace XREngine.Data.Runtime.AotParity;

/// <summary>Classifies the reflective fallback that produced a parity diagnostic.</summary>
public enum EAotParityCategory
{
    /// <summary>A type name resolved by scanning loaded assemblies or by <c>Type.GetType</c> instead of published metadata.</summary>
    TypeResolutionScan = 0,
    /// <summary>A cooked payload deserialized through the reflective authoring reader instead of a registered runtime codec.</summary>
    ReflectiveCookedDeserialization = 1,
    /// <summary>An object constructed through <c>Activator.CreateInstance</c> or a reflected constructor instead of a registered factory.</summary>
    ReflectiveFactory = 2,
    /// <summary>A property, field, method, or event bound through reflection instead of a generated accessor.</summary>
    ReflectiveMemberBinding = 3,
    /// <summary>A polymorphic YAML node resolved its concrete type by scanning loaded assemblies.</summary>
    PolymorphicYamlScan = 4,
}
