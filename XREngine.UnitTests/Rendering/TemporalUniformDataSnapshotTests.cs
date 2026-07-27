using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class TemporalUniformDataSnapshotTests
{
    [Test]
    public void ConcurrentPublication_DoesNotExposeTornTemporalUniformData()
    {
        TemporalUniformDataSnapshot snapshot = new();
        snapshot.Publish(CreateData(1));
        int writersRemaining = 2;

        Task firstWriter = Task.Run(() => PublishRange(snapshot, 2, 20_000, ref writersRemaining));
        Task secondWriter = Task.Run(() => PublishRange(snapshot, 20_001, 40_000, ref writersRemaining));
        Task reader = Task.Run(() =>
        {
            SpinWait spinWait = default;
            do
            {
                snapshot.TryRead(out VPRC_TemporalAccumulationPass.TemporalUniformData data).ShouldBeTrue();
                AssertConsistent(data);
                spinWait.SpinOnce();
            }
            while (Volatile.Read(ref writersRemaining) != 0);

            snapshot.TryRead(out VPRC_TemporalAccumulationPass.TemporalUniformData finalData).ShouldBeTrue();
            AssertConsistent(finalData);
        });

        Task.WaitAll(firstWriter, secondWriter, reader);
    }

    private static void PublishRange(
        TemporalUniformDataSnapshot snapshot,
        int first,
        int last,
        ref int writersRemaining)
    {
        try
        {
            for (int value = first; value <= last; value++)
            {
                VPRC_TemporalAccumulationPass.TemporalUniformData data = CreateData(value);
                snapshot.Publish(data);
            }
        }
        finally
        {
            Interlocked.Decrement(ref writersRemaining);
        }
    }

    private static VPRC_TemporalAccumulationPass.TemporalUniformData CreateData(int value)
        => new()
        {
            Width = (uint)value,
            Height = (uint)(value * 2),
            ProfileGeneration = (ulong)value,
            PrevViewMatrix = Matrix4x4.CreateTranslation(value, -value, value * 3.0f),
        };

    private static void AssertConsistent(
        in VPRC_TemporalAccumulationPass.TemporalUniformData data)
    {
        data.Height.ShouldBe(data.Width * 2u);
        data.ProfileGeneration.ShouldBe(data.Width);
        data.PrevViewMatrix.M42.ShouldBe(-data.PrevViewMatrix.M41);
        data.PrevViewMatrix.M43.ShouldBe(data.PrevViewMatrix.M41 * 3.0f);
    }
}
