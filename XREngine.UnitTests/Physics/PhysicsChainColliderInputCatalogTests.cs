using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using GPUColliderData = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GPUColliderData;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Checks the collider input catalog protocol across the rotating input banks: every bank that
/// the solver reads holds the current colliders, and unchanged frames write nothing.
/// </summary>
[TestFixture]
public sealed class PhysicsChainColliderInputCatalogTests
{
    private const int BankCount = 8;

    /// <summary>One input bank: its page record, its collider buffer, and its mapped memory.</summary>
    private sealed class SimulatedBank(int index)
    {
        public readonly PhysicsChainInputPage Page = new();
        public XRDataBuffer<GPUColliderData> Buffer =
            new($"ColliderBank{index}", EBufferTarget.ShaderStorageBuffer, 16u);
        public GPUColliderData[] Memory = [];
        public int WriteCount;
    }

    /// <summary>The collider fields of one request that the dispatcher passes to the catalog.</summary>
    private sealed class SimulatedRequest(int offset, params GPUColliderData[] colliders)
    {
        public GPUColliderData[] Colliders = colliders;
        public int Offset = offset;
        public int ArenaGeneration = 1;
        public int Signature = Hash(colliders);
        public PhysicsChainColliderCatalogStamp Stamp;

        public void Replace(params GPUColliderData[] colliders)
        {
            Colliders = colliders;
            Signature = Hash(colliders);
        }
    }

    private static GPUColliderData Collider(float x, int type = 0)
        => new() { Center = new Vector4(x, 0.0f, 0.0f, 0.25f), Params = new Vector4(1.0f), Type = type };

    private static int Hash(GPUColliderData[] colliders)
    {
        var hash = new HashCode();
        foreach (GPUColliderData collider in colliders)
        {
            hash.Add(collider.Center);
            hash.Add(collider.Params);
            hash.Add(collider.Orientation);
            hash.Add(collider.Type);
        }
        return hash.ToHashCode();
    }

    private static SimulatedBank[] CreateBanks()
    {
        var banks = new SimulatedBank[BankCount];
        for (int index = 0; index < banks.Length; ++index)
            banks[index] = new SimulatedBank(index);
        return banks;
    }

    /// <summary>Runs one frame in the order of UploadInputPageColliders on one bank.</summary>
    private static void RunFrame(PhysicsChainColliderInputCatalog catalog, SimulatedBank bank,
        int arenaHighWater, params SimulatedRequest[] requests)
    {
        catalog.Resize(arenaHighWater);
        foreach (SimulatedRequest request in requests)
            catalog.TryStage(request.Colliders, request.Offset, request.ArenaGeneration,
                request.Signature, ref request.Stamp).ShouldBeTrue();
        if (!catalog.PageNeedsWrite(bank.Page, bank.Buffer))
            return;
        bank.Memory = catalog.Rows.ToArray();
        ++bank.WriteCount;
        catalog.MarkPageWritten(bank.Page, bank.Buffer);
    }

    private static void ShouldHold(SimulatedBank bank, SimulatedRequest request)
    {
        for (int index = 0; index < request.Colliders.Length; ++index)
            bank.Memory[request.Offset + index].ShouldBe(request.Colliders[index]);
    }

    private static void ResetWriteCounts(SimulatedBank[] banks)
    {
        foreach (SimulatedBank bank in banks)
            bank.WriteCount = 0;
    }

    [Test]
    public void ColliderChange_ReachesEveryRotatingBankOnce()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        SimulatedBank[] banks = CreateBanks();
        var first = new SimulatedRequest(0, Collider(1.0f), Collider(2.0f, type: 1));
        var second = new SimulatedRequest(2, Collider(3.0f), Collider(4.0f, type: 1));
        foreach (SimulatedBank bank in banks)
            RunFrame(catalog, bank, 4, first, second);
        ResetWriteCounts(banks);

        first.Replace(Collider(5.0f), Collider(6.0f, type: 1));
        // Two full rotations: each bank is out of date once, then current.
        for (int frame = 0; frame < BankCount * 2; ++frame)
            RunFrame(catalog, banks[frame % BankCount], 4, first, second);

        foreach (SimulatedBank bank in banks)
        {
            bank.WriteCount.ShouldBe(1);
            ShouldHold(bank, first);
            ShouldHold(bank, second);
        }
    }

    [Test]
    public void UnchangedFrames_WriteNoBankAndKeepTheVersion()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        SimulatedBank[] banks = CreateBanks();
        var request = new SimulatedRequest(0, Collider(1.0f));
        foreach (SimulatedBank bank in banks)
            RunFrame(catalog, bank, 1, request);
        ResetWriteCounts(banks);
        ulong version = catalog.Version;

        for (int frame = 0; frame < BankCount * 3; ++frame)
            RunFrame(catalog, banks[frame % BankCount], 1, request);

        catalog.Version.ShouldBe(version);
        foreach (SimulatedBank bank in banks)
            bank.WriteCount.ShouldBe(0);
    }

    [Test]
    public void ReplacedBankBuffer_ReceivesAFullWrite()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        var bank = new SimulatedBank(0);
        var request = new SimulatedRequest(0, Collider(1.0f));
        RunFrame(catalog, bank, 1, request);

        // Growth or quarantine replaces the mapped buffer; its memory holds nothing.
        bank.Buffer = new XRDataBuffer<GPUColliderData>("ColliderBank0Successor", EBufferTarget.ShaderStorageBuffer, 32u);
        bank.Memory = [];
        bank.WriteCount = 0;
        RunFrame(catalog, bank, 1, request);

        bank.WriteCount.ShouldBe(1);
        ShouldHold(bank, request);
    }

    [Test]
    public void Invalidate_RewritesEveryBankOnce()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        SimulatedBank[] banks = CreateBanks();
        var request = new SimulatedRequest(0, Collider(1.0f));
        foreach (SimulatedBank bank in banks)
            RunFrame(catalog, bank, 1, request);
        ResetWriteCounts(banks);

        // A rolled-back batch invalidates every bank.
        catalog.Invalidate();
        for (int frame = 0; frame < BankCount * 2; ++frame)
            RunFrame(catalog, banks[frame % BankCount], 1, request);

        foreach (SimulatedBank bank in banks)
        {
            bank.WriteCount.ShouldBe(1);
            ShouldHold(bank, request);
        }
    }

    [Test]
    public void ArenaReallocation_CopiesTheRowsToTheNewSlice()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        var bank = new SimulatedBank(0);
        var moving = new SimulatedRequest(0, Collider(1.0f), Collider(2.0f));
        RunFrame(catalog, bank, 2, moving);

        // The arena moves the request to a new slice with the same collider content.
        moving.Offset = 4;
        ++moving.ArenaGeneration;
        RunFrame(catalog, bank, 6, moving);

        bank.WriteCount.ShouldBe(2);
        ShouldHold(bank, moving);
    }

    [Test]
    public void ClearedStamp_CopiesUnchangedContentAgain()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        var request = new SimulatedRequest(0, Collider(1.0f));
        catalog.Resize(1);
        catalog.TryStage(request.Colliders, request.Offset, request.ArenaGeneration,
            request.Signature, ref request.Stamp).ShouldBeTrue();
        ulong staged = catalog.Version;

        // InvalidateUploadedInputs clears the stamp after uncertain native execution.
        request.Stamp = default;
        catalog.TryStage(request.Colliders, request.Offset, request.ArenaGeneration,
            request.Signature, ref request.Stamp).ShouldBeTrue();

        catalog.Version.ShouldBeGreaterThan(staged);
        request.Stamp.Valid.ShouldBeTrue();
    }

    [Test]
    public void OutOfRangeSlice_FailsWithoutChangingTheCatalog()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        var request = new SimulatedRequest(1, Collider(1.0f), Collider(2.0f));
        catalog.Resize(2);
        ulong version = catalog.Version;

        catalog.TryStage(request.Colliders, request.Offset, request.ArenaGeneration,
            request.Signature, ref request.Stamp).ShouldBeFalse();

        catalog.Version.ShouldBe(version);
        request.Stamp.Valid.ShouldBeFalse();
    }

    [Test]
    public void Resize_AdvancesTheVersionAndAnEmptyCatalogNeedsNoWrite()
    {
        var catalog = new PhysicsChainColliderInputCatalog();
        var bank = new SimulatedBank(0);
        catalog.PageNeedsWrite(bank.Page, bank.Buffer).ShouldBeFalse();

        ulong version = catalog.Version;
        catalog.Resize(3);
        catalog.Version.ShouldBeGreaterThan(version);
        catalog.PageNeedsWrite(bank.Page, bank.Buffer).ShouldBeTrue();

        version = catalog.Version;
        catalog.Resize(3);
        catalog.Version.ShouldBe(version);
    }
}
