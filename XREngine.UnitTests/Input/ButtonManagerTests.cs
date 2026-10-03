using NUnit.Framework;
using Shouldly;
using XREngine.Input.Devices;

namespace XREngine.UnitTests.Input;

public sealed class ButtonManagerTests
{
    [Test]
    public void PressCallbacksChangingSubscriptions_UseStableDispatchSnapshot()
    {
        ButtonManager button = new(0, "test button");
        int firstCalls = 0;
        int secondCalls = 0;
        int addedCalls = 0;
        Action? first = null;
        Action second = () => secondCalls++;
        Action added = () => addedCalls++;
        first = () =>
        {
            firstCalls++;
            button.Register(first!, EButtonInputType.Pressed, unregister: true);
            button.Register(second, EButtonInputType.Pressed, unregister: true);
            button.Register(added, EButtonInputType.Pressed, unregister: false);
        };
        button.Register(first, EButtonInputType.Pressed, unregister: false);
        button.Register(second, EButtonInputType.Pressed, unregister: false);

        button.OnPressed();
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(0);

        button.OnPressed();
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(1);
    }

    [Test]
    public void StateCallbacksChangingSubscriptions_UseStableDispatchSnapshot()
    {
        ButtonManager button = new(0, "test button");
        int firstCalls = 0;
        int secondCalls = 0;
        int addedCalls = 0;
        DelButtonState? first = null;
        DelButtonState second = _ => secondCalls++;
        DelButtonState added = _ => addedCalls++;
        first = _ =>
        {
            firstCalls++;
            button.RegisterPressedState(first!, unregister: true);
            button.RegisterPressedState(second, unregister: true);
            button.RegisterPressedState(added, unregister: false);
        };
        button.RegisterPressedState(first, unregister: false);
        button.RegisterPressedState(second, unregister: false);

        button.OnPressed();
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(0);

        button.OnReleased();
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(1);
    }
}
