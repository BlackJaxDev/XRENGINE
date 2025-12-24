namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private sealed class ComponentTypeDescriptor(Type type, string displayName, string ns, string assemblyName)
    {
        public Type Type { get; } = type;
        public string DisplayName { get; } = displayName;
        public string Namespace { get; } = ns;
        public string AssemblyName { get; } = assemblyName;
        public string FullName { get; } = type.FullName ?? type.Name;
    }
}
