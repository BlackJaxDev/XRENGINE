using System.Text;

namespace XREngine.Rendering.Shaders;

/// <summary>
/// Writes GLSL struct declarations for fixed GPU records and dynamic material rows.
/// Callers provide members in their physical storage order.
/// </summary>
public static class GpuRecordGlslDeclaration
{
    public static void AppendStruct<TMember>(
        StringBuilder source,
        string name,
        IReadOnlyList<TMember> members,
        Func<TMember, string> typeSelector,
        Func<TMember, string> nameSelector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(typeSelector);
        ArgumentNullException.ThrowIfNull(nameSelector);

        source.Append("struct ").AppendLine(name);
        source.AppendLine("{");
        for (int index = 0; index < members.Count; ++index)
        {
            TMember member = members[index];
            source.Append("    ")
                .Append(typeSelector(member))
                .Append(' ')
                .Append(nameSelector(member))
                .AppendLine(";");
        }
        source.AppendLine("};");
    }
}
