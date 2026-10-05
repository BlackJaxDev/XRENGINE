using NUnit.Framework;
using Shouldly;
using XREngine.Input.Devices;

namespace XREngine.UnitTests.Input;

public sealed class AxisManagerTests
{
    [Test]
    public void CallbackChangesSubscriptions_AffectNextDispatchWithoutInvalidatingCurrentDispatch()
    {
        AxisManager axis = new(0, "test axis");
        int firstCalls = 0;
        int secondCalls = 0;
        int addedCalls = 0;
        DelAxisValue? first = null;
        DelAxisValue second = _ => secondCalls++;
        DelAxisValue added = _ => addedCalls++;
        first = _ =>
        {
            firstCalls++;
            axis.RegisterAxis(first!, continuousUpdate: true, unregister: true);
            axis.RegisterAxis(second, continuousUpdate: true, unregister: true);
            axis.RegisterAxis(added, continuousUpdate: true, unregister: false);
        };
        axis.RegisterAxis(first, continuousUpdate: true, unregister: false);
        axis.RegisterAxis(second, continuousUpdate: true, unregister: false);

        axis.Tick(0.5f, 1.0f / 60.0f);
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(0);

        axis.Tick(0.5f, 1.0f / 60.0f);
        firstCalls.ShouldBe(1);
        secondCalls.ShouldBe(1);
        addedCalls.ShouldBe(1);
    }
}
