using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// API-boundary guard: shared gameplay components may consume backend implementations,
/// but cannot accidentally publish native backend types as their portable contract.
/// </summary>
public sealed class PhysicsGameplayApiBoundaryTests
{
    [Test]
    public void PublicGameplayComponentApi_DoesNotLeakBackendTypesOutsideNamedExtensions()
    {
        Assembly assembly = typeof(XRComponent).Assembly;
        List<string> leaks = [];

        foreach (Type type in assembly.GetExportedTypes()
                     .Where(static type => type.Namespace?.StartsWith("XREngine.Components", StringComparison.Ordinal) == true))
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.DeclaredOnly;

            foreach (PropertyInfo property in type.GetProperties(flags))
                CheckMember(type, property, property.PropertyType, leaks);

            foreach (FieldInfo field in type.GetFields(flags))
                CheckMember(type, field, field.FieldType, leaks);

            foreach (EventInfo eventInfo in type.GetEvents(flags))
                CheckMember(type, eventInfo, eventInfo.EventHandlerType, leaks);

            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.IsSpecialName)
                    continue;

                CheckMember(type, method, method.ReturnType, leaks);
                foreach (ParameterInfo parameter in method.GetParameters())
                    CheckMember(type, method, parameter.ParameterType, leaks, parameter.Name);
            }
        }

        leaks.ShouldBeEmpty(string.Join(Environment.NewLine, leaks));
    }

    private static void CheckMember(
        Type declaringType,
        MemberInfo member,
        Type? exposedType,
        ICollection<string> leaks,
        string? parameterName = null)
    {
        if (exposedType is null || !ContainsBackendType(exposedType) || IsExplicitBackendExtension(declaringType, member))
            return;

        string suffix = parameterName is null ? string.Empty : $" parameter '{parameterName}'";
        leaks.Add($"{declaringType.FullName}.{member.Name}{suffix} exposes {exposedType.FullName}.");
    }

    private static bool IsExplicitBackendExtension(Type declaringType, MemberInfo member)
        => IsBackendNamed(declaringType.Name)
            || (IsBackendNamed(member.Name) && member.Name.Contains("Extension", StringComparison.Ordinal));

    private static bool ContainsBackendType(Type type)
    {
        if (type.HasElementType)
            return ContainsBackendType(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericArguments().Any(ContainsBackendType))
            return true;

        string fullName = type.FullName ?? type.Name;
        return fullName.StartsWith("MagicPhysX.", StringComparison.Ordinal)
            || fullName.Contains(".Physx", StringComparison.Ordinal)
            || fullName.Contains(".Jolt", StringComparison.Ordinal)
            || type.Name.StartsWith("Px", StringComparison.Ordinal);
    }

    private static bool IsBackendNamed(string name)
        => name.Contains("Physx", StringComparison.Ordinal)
            || name.Contains("Jolt", StringComparison.Ordinal);
}
