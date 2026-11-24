using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Cutscenes;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneCurveTests
{
    private static CutsceneTime T(double seconds) => CutsceneTime.FromSeconds(seconds);

    private static CutsceneFloatCurve MakeFloat(ECutsceneCurveInterpolation interpolation)
        => new()
        {
            Interpolation = interpolation,
            Times = [T(1), T(3)],
            Values = [10f, 20f],
            DefaultValue = -5f,
        };

    [Test]
    public void Float_ExactKeyTimes_ReturnKeyValues()
    {
        CutsceneFloatCurve curve = MakeFloat(ECutsceneCurveInterpolation.Linear);
        curve.Sample(T(1)).ShouldBe(10f);
        curve.Sample(T(3)).ShouldBe(20f);
    }

    [Test]
    public void Float_Linear_MidpointIsAverage()
        => MakeFloat(ECutsceneCurveInterpolation.Linear).Sample(T(2)).ShouldBe(15f, 1e-5f);

    [Test]
    public void Float_Step_HoldsEarlierKey()
    {
        CutsceneFloatCurve curve = MakeFloat(ECutsceneCurveInterpolation.Step);
        curve.Sample(T(2)).ShouldBe(10f);
        curve.Sample(T(2.999)).ShouldBe(10f);
        curve.Sample(T(3)).ShouldBe(20f);
    }

    [Test]
    public void Float_OutOfRange_HoldsEndKeys()
    {
        CutsceneFloatCurve curve = MakeFloat(ECutsceneCurveInterpolation.Linear);
        curve.Sample(T(0)).ShouldBe(10f);
        curve.Sample(T(100)).ShouldBe(20f);
    }

    [Test]
    public void Float_Empty_ReturnsDefault()
        => new CutsceneFloatCurve { DefaultValue = 7f }.Sample(T(1)).ShouldBe(7f);

    [Test]
    public void Float_ManyKeys_FindsCorrectSegment()
    {
        CutsceneTime[] times = new CutsceneTime[100];
        float[] values = new float[100];
        for (int i = 0; i < 100; i++)
        {
            times[i] = T(i);
            values[i] = i * 2f;
        }
        CutsceneFloatCurve curve = new() { Times = times, Values = values };
        curve.Sample(T(57.5)).ShouldBe(115f, 1e-3f);
    }

    [Test]
    public void Vector3_LinearMidpointStepEmptyAndRange()
    {
        CutsceneVector3Curve linear = new()
        {
            Interpolation = ECutsceneCurveInterpolation.Linear,
            Times = [T(0), T(2)],
            Values = [Vector3.Zero, new Vector3(2, 4, 6)],
            DefaultValue = Vector3.One,
        };
        linear.Sample(T(1)).ShouldBe(new Vector3(1, 2, 3));
        linear.Sample(T(2)).ShouldBe(new Vector3(2, 4, 6));
        linear.Sample(T(-1)).ShouldBe(Vector3.Zero);
        linear.Sample(T(9)).ShouldBe(new Vector3(2, 4, 6));

        linear.Interpolation = ECutsceneCurveInterpolation.Step;
        linear.Sample(T(1)).ShouldBe(Vector3.Zero);

        new CutsceneVector3Curve { DefaultValue = Vector3.One }.Sample(T(1)).ShouldBe(Vector3.One);
    }

    [Test]
    public void Quaternion_LinearUsesSlerp_StepHoldsEmptyDefault()
    {
        Quaternion a = Quaternion.Identity;
        Quaternion b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        CutsceneQuaternionCurve curve = new()
        {
            Interpolation = ECutsceneCurveInterpolation.Linear,
            Times = [T(0), T(2)],
            Values = [a, b],
        };
        Quaternion mid = curve.Sample(T(1));
        Quaternion expected = Quaternion.Slerp(a, b, 0.5f);
        MathF.Abs(Quaternion.Dot(mid, expected)).ShouldBe(1f, 1e-5f);
        curve.Sample(T(0)).ShouldBe(a);
        curve.Sample(T(5)).ShouldBe(b);

        curve.Interpolation = ECutsceneCurveInterpolation.Step;
        curve.Sample(T(1)).ShouldBe(a);

        new CutsceneQuaternionCurve().Sample(T(1)).ShouldBe(Quaternion.Identity);
    }

    [Test]
    public void Sample_AllocatesZeroBytes()
    {
        CutsceneFloatCurve f = MakeFloat(ECutsceneCurveInterpolation.Linear);
        CutsceneVector3Curve v = new() { Times = [T(0), T(2)], Values = [Vector3.Zero, Vector3.One] };
        CutsceneQuaternionCurve q = new() { Times = [T(0), T(2)], Values = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f)] };
        CutsceneTime t = T(1.5);

        // Warm up so JIT work is not counted.
        _ = f.Sample(t);
        _ = v.Sample(t);
        _ = q.Sample(t);

        float sink = 0f;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            sink += f.Sample(t);
            sink += v.Sample(t).X;
            sink += q.Sample(t).W;
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0L);
        float.IsNaN(sink).ShouldBeFalse();
    }
}
