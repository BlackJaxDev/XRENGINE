using System.Reflection;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine;

internal static class RuntimeAnimationComponentActivator
{
    private const string AnimationClipComponentTypeName = "XREngine.Components.Animation.AnimationClipComponent";
    private static readonly string[] CandidateAssemblyNames = ["XREngine.Runtime.AnimationIntegration", "XRENGINE"];

    private static volatile Type? _animationClipComponentType;
    private static volatile bool _animationClipComponentResolved;

    public static XRComponent? AddAnimationClipComponent(SceneNode node, AnimationClip clip)
    {
        Type? componentType = ResolveAnimationClipComponentType();
        if (componentType is null)
        {
            Debug.LogWarning($"Unable to attach animation clip '{clip.Name}' because {AnimationClipComponentTypeName} is unavailable.");
            return null;
        }

        XRComponent? component = node.AddComponent(componentType);
        if (component is null)
            return null;

        component.Name = clip.Name;
        componentType.GetProperty("Animation", BindingFlags.Instance | BindingFlags.Public)?.SetValue(component, clip);
        return component;
    }

    public static int CountAnimationClipComponents(SceneNode node)
    {
        Type? componentType = ResolveAnimationClipComponentType();
        if (componentType is not null)
            return node.GetComponents(componentType).Count();

        return node.GetComponents<XRComponent>().Count(static component => component.GetType().FullName == AnimationClipComponentTypeName);
    }

    private static Type? ResolveAnimationClipComponentType()
    {
        if (_animationClipComponentResolved)
            return _animationClipComponentType;

        foreach (string assemblyName in CandidateAssemblyNames)
        {
            Type? resolvedType = Type.GetType($"{AnimationClipComponentTypeName}, {assemblyName}", throwOnError: false);
            if (resolvedType is not null)
            {
                _animationClipComponentType = resolvedType;
                _animationClipComponentResolved = true;
                return resolvedType;
            }
        }

        _animationClipComponentResolved = true;
        return null;
    }
}