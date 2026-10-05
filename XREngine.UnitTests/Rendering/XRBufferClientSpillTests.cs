using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Spill handoff between a buffer's private client copy and its file mapping:
/// clones own their bytes, a spill read never sees its source freed, and any write
/// during the spill abandons the swap.
/// </summary>
[TestFixture]
public sealed class XRBufferClientSpillTests
{
    private const uint SpillFloatCount = 128u * 1024u; // 512 KiB, above the spill minimum

    [Test]
    public unsafe void SpilledSourceClone_OwnsACopyOfTheBytes()
    {
        using XRBufferSpilledDataSource spilled = MapTempFile(1024u, fill: 0x5A);

        using DataSource clone = spilled.Clone();

        clone.External.ShouldBeFalse();
        ((nint)clone.Address).ShouldNotBe((nint)spilled.Address);
        clone.AsReadOnlySpan().SequenceEqual(spilled.AsReadOnlySpan()).ShouldBeTrue();
    }

    [Test]
    public void DisposedSpilledSource_ReportsNoAddress()
    {
        XRBufferSpilledDataSource spilled = MapTempFile(1024u, fill: 0x11);

        spilled.Dispose();

        ((nint)spilled.Address).ShouldBe(0);
    }

    [Test]
    public void DisposingTheBufferDuringASpillRead_DefersFreeingTheCopy()
    {
        XRDataBuffer buffer = CreateUploadedSpillCandidate("DisposeDuringSpill");
        buffer.TryBeginClientSpill(out DataSource source, out ulong revision, out ulong writeActivity).ShouldBeTrue();

        buffer.Dispose();

        // The spill thread is still reading the copy; it must stay allocated.
        ((nint)source.Address).ShouldNotBe(0);
        buffer.TryCompleteClientSpill(source, null, revision, writeActivity, out bool disposeSource).ShouldBeFalse();
        disposeSource.ShouldBeTrue();
        source.Dispose();
    }

    [Test]
    public void LegacyWriteDuringASpill_AbandonsTheSwap()
    {
        XRDataBuffer buffer = CreateUploadedSpillCandidate("LegacyWriteDuringSpill");
        buffer.TryBeginClientSpill(out DataSource source, out ulong revision, out ulong writeActivity).ShouldBeTrue();

        buffer.SetFloat(0u, 42.0f);

        using XRBufferSpilledDataSource spilled = MapTempFile(source.Length, fill: 0);
        buffer.TryCompleteClientSpill(source, spilled, revision, writeActivity, out bool disposeSource).ShouldBeFalse();
        disposeSource.ShouldBeFalse();
        buffer.IsClientCopySpilled.ShouldBeFalse();
        buffer.GetFloat(0u).ShouldBe(42.0f);
    }

    [Test]
    public void ScopedWriterInFlight_BlocksTheSpill()
    {
        XRDataBuffer buffer = CreateUploadedSpillCandidate("WriterInFlight");

        XRBufferWriter<float> writer = buffer.AllocAt<float>(0u, 4u, XRBufferWriteOptions.FromBuffer(buffer).WithWriteMode(XRBufferWriteMode.Preserve));
        buffer.TryBeginClientSpill(out _, out _, out _).ShouldBeFalse();
        writer.Cancel();

        buffer.TryBeginClientSpill(out DataSource source, out ulong revision, out ulong writeActivity).ShouldBeTrue();
        buffer.TryCompleteClientSpill(source, null, revision, writeActivity, out _).ShouldBeFalse();
    }

    [Test]
    public void DynamicUsageBuffer_IsNotSpilled()
    {
        XRDataBuffer buffer = CreateUploadedSpillCandidate("DynamicUsage", EBufferUsage.DynamicDraw);

        buffer.TryBeginClientSpill(out _, out _, out _).ShouldBeFalse();
    }

    private static XRDataBuffer CreateUploadedSpillCandidate(string name, EBufferUsage usage = EBufferUsage.StaticDraw)
    {
        XRDataBuffer buffer = new(name, EBufferTarget.ArrayBuffer, SpillFloatCount, EComponentType.Float, 1u, false, false)
        {
            Usage = usage,
            ClientCopyPolicy = EXRBufferClientCopyPolicy.ReleaseAfterUpload,
        };
        buffer.ReportBackendUploadState(buffer.Length, buffer.Length, hasPendingUpload: false, XRBufferResolvedRoute.Unknown, readyForGpuUse: true);
        return buffer;
    }

    private static XRBufferSpilledDataSource MapTempFile(uint length, byte fill)
    {
        string path = Path.Combine(Path.GetTempPath(), $"xre-spill-test-{Guid.NewGuid():N}.bin");
        FileStream file = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        byte[] bytes = new byte[length];
        Array.Fill(bytes, fill);
        file.Write(bytes);
        file.Flush();
        return XRBufferSpilledDataSource.Map(file, length);
    }
}
