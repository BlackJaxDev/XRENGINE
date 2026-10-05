using System.Reflection;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Input;

[NonParallelizable]
public sealed class EngineRuntimeVrInputServicesTests
{
    [Test]
    public void CallbackChanges_AffectNextDispatchWithoutInvalidatingCurrentDispatch()
    {
        using EngineRuntimeVrInputServices input = new();
        int firstCalls = 0;
        int secondCalls = 0;
        int addedCalls = 0;
        Action<bool>? first = null;
        Action<bool> second = pressed => { if (pressed) secondCalls++; };
        Action<bool> added = pressed => { if (pressed) addedCalls++; };
        first = pressed =>
        {
            if (!pressed) return;
            firstCalls++;
            input.RegisterBoolAction("Test", "Select", first!, unregister: true);
            input.RegisterBoolAction("Test", "Select", second, unregister: true);
            input.RegisterBoolAction("Test", "Select", added, unregister: false);
        };
        input.RegisterBoolAction("Test", "Select", first, unregister: false);
        input.RegisterBoolAction("Test", "Select", second, unregister: false);

        object registration = FirstBoolRegistration(input);
        DispatchScoped(input, () => DispatchBool(registration, true));
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(0);

        object nextRegistration = FirstBoolRegistration(input);
        DispatchScoped(input, () => DispatchBool(nextRegistration, false));
        DispatchScoped(input, () => DispatchBool(nextRegistration, true));
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(1);
    }

    [Test]
    public void RemovingAnotherActionDuringDispatch_DoesNotInvalidateCurrentSnapshot()
    {
        using EngineRuntimeVrInputServices input = new();
        int removedCalls = 0;
        Action<bool> removed = pressed => { if (pressed) removedCalls++; };
        input.RegisterBoolAction("Test", "First", pressed =>
        {
            if (pressed)
                input.RegisterBoolAction("Test", "Second", removed, unregister: true);
        }, unregister: false);
        input.RegisterBoolAction("Test", "Second", removed, unregister: false);

        Array current = BoolSnapshot(input);
        DispatchScoped(input, () =>
        {
            DispatchBool(FindBoolRegistration(current, "First"), true);
            DispatchBool(FindBoolRegistration(current, "Second"), true);
        });

        removedCalls.ShouldBe(1);
        BoolSnapshot(input).Length.ShouldBe(1);
    }

    [Test]
    public void InputLoss_ReleasesOnlyPreviouslyActiveActions()
    {
        using EngineRuntimeVrInputServices input = new();
        List<bool> values = [];
        input.RegisterBoolAction("Test", "Select", values.Add, unregister: false);
        object registration = FirstBoolRegistration(input);
        MethodInfo release = typeof(EngineRuntimeVrInputServices).GetMethod(
            "DispatchNeutralActions", BindingFlags.Instance | BindingFlags.NonPublic)!;

        release.Invoke(input, null);
        values.Count.ShouldBe(0);
        DispatchBool(registration, true);
        release.Invoke(input, null);
        release.Invoke(input, null);
        values.Count.ShouldBe(2);
        values[0].ShouldBeTrue();
        values[1].ShouldBeFalse();
    }

    private static object FirstBoolRegistration(EngineRuntimeVrInputServices input)
    {
        return BoolSnapshot(input).GetValue(0)!;
    }

    private static object FindBoolRegistration(Array snapshot, string name)
    {
        foreach (object registration in snapshot)
        {
            if ((string)registration.GetType().GetProperty("Name")!.GetValue(registration)! == name)
                return registration;
        }
        throw new AssertionException($"No registration named {name} was found.");
    }

    private static Array BoolSnapshot(EngineRuntimeVrInputServices input)
    {
        FieldInfo snapshot = typeof(EngineRuntimeVrInputServices).GetField(
            "_boolSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Array)snapshot.GetValue(input)!;
    }

    private static void DispatchBool(object registration, bool pressed)
    {
        registration.GetType().GetMethod("Dispatch")!.Invoke(registration, [pressed]);
    }

    private static void DispatchScoped(EngineRuntimeVrInputServices input, Action dispatch)
    {
        Type type = typeof(EngineRuntimeVrInputServices);
        type.GetMethod("BeginActionDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, null);
        try
        {
            dispatch();
        }
        finally
        {
            type.GetMethod("EndActionDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, null);
        }
    }
}
