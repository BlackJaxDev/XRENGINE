using System;

namespace XREngine.Serialization
{
    /// <summary>
    /// Marks an <see cref="YamlDotNet.Serialization.IYamlTypeConverter"/> so it can be
    /// auto-registered when the engine builds its YAML serializer and deserializer.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class YamlTypeConverterAttribute : Attribute
    {
    }
}
