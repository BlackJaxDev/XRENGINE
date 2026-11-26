using System.Reflection;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// The member lists that the native inspector shows for types that have a custom ImGui editor. A custom ImGui
/// editor draws a chosen set of members instead of every public property; a profile gives the native stacked
/// view the same set, in the same order, with the same labels. A type without a profile lists all its own
/// inspectable members.
/// </summary>
internal static class InspectorMemberProfiles
{
    /// <summary>One member of a profile: the property name and the label to show, or null for the default label.</summary>
    public readonly record struct Member(string Name, string? Label = null);

    private static readonly (Type Type, Member[] Members)[] Profiles =
    [
        // The rigid body transform editor: interpolation, the offsets, and the physics pose it reports.
        (typeof(RigidBodyTransform),
        [
            new(nameof(RigidBodyTransform.InterpolationMode)),
            new(nameof(RigidBodyTransform.PositionOffset)),
            new(nameof(RigidBodyTransform.PreRotationOffset)),
            new(nameof(RigidBodyTransform.PostRotationOffset)),
            new(nameof(RigidBodyTransform.LastPosition), "Physics Position"),
            new(nameof(RigidBodyTransform.LastRotation), "Physics Rotation"),
        ]),
        // The standard transform editor: translation, rotation as pitch, yaw, and roll in degrees, scale, and order.
        (typeof(Transform),
        [
            new(nameof(Transform.Translation)),
            new(nameof(Transform.Rotator), "Rotation"),
            new(nameof(Transform.Scale)),
            new(nameof(Transform.Order)),
        ]),
    ];

    /// <summary>
    /// Gets the profile of <paramref name="type"/>: the profile of the type or its nearest base type that has
    /// one, and that profile's type. Returns false when no profile applies.
    /// </summary>
    public static bool TryGet(Type type, out Type profileType, out Member[] members)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            foreach ((Type candidate, Member[] profile) in Profiles)
            {
                if (candidate != current)
                    continue;
                profileType = candidate;
                members = profile;
                return true;
            }
        }
        profileType = typeof(object);
        members = [];
        return false;
    }

    /// <summary>
    /// Orders <paramref name="members"/> by the profile of <paramref name="type"/>: the profile members first, in
    /// profile order, then the members that a type derived from the profile type declares. Other members are
    /// left out. Without a profile, the list does not change.
    /// </summary>
    public static List<PropertyInfo> Apply(Type type, List<PropertyInfo> members)
    {
        if (!TryGet(type, out Type profileType, out Member[] profile))
            return members;
        List<PropertyInfo> ordered = [];
        foreach (Member member in profile)
        {
            PropertyInfo? match = members.Find(property => property.Name == member.Name);
            if (match is not null)
                ordered.Add(match);
        }
        foreach (PropertyInfo property in members)
            if (property.DeclaringType is { } declaring && declaring != profileType &&
                profileType.IsAssignableFrom(declaring) && !ordered.Contains(property))
                ordered.Add(property);
        return ordered;
    }

    /// <summary>Gets the profile label of a member of <paramref name="type"/>, or null when it has none.</summary>
    public static string? LabelFor(Type type, string memberName)
    {
        if (!TryGet(type, out _, out Member[] profile))
            return null;
        foreach (Member member in profile)
            if (member.Name == memberName)
                return member.Label;
        return null;
    }
}
