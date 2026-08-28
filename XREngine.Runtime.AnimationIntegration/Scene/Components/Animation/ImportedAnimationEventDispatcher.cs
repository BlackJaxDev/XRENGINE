using System.Collections.Concurrent;
using System.Reflection;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>Dispatches native Unity animation events to components on the animated node.</summary>
internal static class ImportedAnimationEventDispatcher
{
    private enum EArgumentKind : byte
    {
        None,
        Float,
        Int,
        String,
        AssetReference,
        Event,
        Occurrence,
        Unsupported,
    }

    private readonly record struct DispatchKey(Type Type, string FunctionName);
    private readonly record struct DispatchPlan(MethodInfo? Method, EArgumentKind ArgumentKind);

    private static readonly ConcurrentDictionary<DispatchKey, DispatchPlan> Plans = new();

    public static int Dispatch(XRComponent owner, in ImportedAnimationEventOccurrence occurrence)
    {
        int receiverCount = 0;
        foreach (XRComponent component in owner.SceneNode.Components)
        {
            if (component is IImportedAnimationEventReceiver typedReceiver)
            {
                typedReceiver.ReceiveImportedAnimationEvent(occurrence);
                receiverCount++;
                continue;
            }

            DispatchPlan plan = Plans.GetOrAdd(
                new DispatchKey(component.GetType(), occurrence.Event.FunctionName),
                static key => CreatePlan(key.Type, key.FunctionName));
            if (plan.Method is null || plan.ArgumentKind == EArgumentKind.Unsupported)
                continue;

            Invoke(component, plan, occurrence);
            receiverCount++;
        }

        return receiverCount;
    }

    private static DispatchPlan CreatePlan(Type componentType, string functionName)
    {
        MethodInfo? bestMethod = null;
        EArgumentKind bestKind = EArgumentKind.Unsupported;
        foreach (MethodInfo method in componentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!method.Name.Equals(functionName, StringComparison.Ordinal) || method.ContainsGenericParameters)
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            EArgumentKind kind = parameters.Length switch
            {
                0 => EArgumentKind.None,
                1 => GetArgumentKind(parameters[0].ParameterType),
                _ => EArgumentKind.Unsupported,
            };
            if (kind == EArgumentKind.Unsupported
                || (bestMethod is not null && CompareCandidates(method, kind, bestMethod, bestKind) <= 0))
                continue;

            bestMethod = method;
            bestKind = kind;
        }

        return new DispatchPlan(bestMethod, bestKind);
    }

    private static int CompareCandidates(
        MethodInfo candidate,
        EArgumentKind candidateKind,
        MethodInfo current,
        EArgumentKind currentKind)
    {
        int kindComparison = candidateKind.CompareTo(currentKind);
        if (kindComparison != 0)
            return kindComparison;

        // Reflection does not promise source-order enumeration. Metadata tokens are
        // stable within one compiled module, so overload selection stays deterministic.
        int moduleComparison = string.CompareOrdinal(candidate.Module.ScopeName, current.Module.ScopeName);
        if (moduleComparison != 0)
            return -moduleComparison;
        return current.MetadataToken.CompareTo(candidate.MetadataToken);
    }

    private static EArgumentKind GetArgumentKind(Type parameterType)
    {
        if (parameterType == typeof(ImportedAnimationEventOccurrence))
            return EArgumentKind.Occurrence;
        if (parameterType == typeof(ImportedAnimationEvent))
            return EArgumentKind.Event;
        if (parameterType == typeof(SourceAssetReference))
            return EArgumentKind.AssetReference;
        if (parameterType == typeof(string))
            return EArgumentKind.String;
        if (parameterType == typeof(int))
            return EArgumentKind.Int;
        if (parameterType == typeof(float))
            return EArgumentKind.Float;
        return EArgumentKind.Unsupported;
    }

    private static void Invoke(
        XRComponent component,
        DispatchPlan plan,
        in ImportedAnimationEventOccurrence occurrence)
    {
        switch (plan.ArgumentKind)
        {
            case EArgumentKind.None:
                plan.Method!.Invoke(component, null);
                break;
            case EArgumentKind.Float:
                plan.Method!.Invoke(component, [occurrence.Event.FloatParameter]);
                break;
            case EArgumentKind.Int:
                plan.Method!.Invoke(component, [occurrence.Event.IntParameter]);
                break;
            case EArgumentKind.String:
                plan.Method!.Invoke(component, [occurrence.Event.StringParameter]);
                break;
            case EArgumentKind.AssetReference:
                plan.Method!.Invoke(component, [occurrence.Event.ObjectReferenceParameter]);
                break;
            case EArgumentKind.Event:
                plan.Method!.Invoke(component, [occurrence.Event]);
                break;
            case EArgumentKind.Occurrence:
                plan.Method!.Invoke(component, [occurrence]);
                break;
        }
    }
}
