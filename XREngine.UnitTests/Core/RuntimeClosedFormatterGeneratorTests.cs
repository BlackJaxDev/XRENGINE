using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

/// <summary>Checks generated formatter examples and runtime paths that do not need them.</summary>
[TestFixture]
[NonParallelizable]
public sealed class RuntimeClosedFormatterGeneratorTests
{
    [Test]
    public void GeneratedList_CreatesIndependentCollectionsAndReleasesRegistration()
    {
        Type type = typeof(List<XRPersistentCall>);
        Assert.That(CookedBinaryFormatterRegistry.TryCreateCollection(type, out _), Is.False);
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(CookedBinaryFormatterRegistry.TryResolve(type.FullName!, out Type? resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(type));
            Assert.That(CookedBinaryFormatterRegistry.TryCreateCollection(type, out object? first), Is.True);
            Assert.That(CookedBinaryFormatterRegistry.TryCreateCollection(type, out object? second), Is.True);
            Assert.That(first, Is.TypeOf<List<XRPersistentCall>>());
            ((List<XRPersistentCall>)first!).Add(new XRPersistentCall { MethodName = "First" });
            Assert.That(second, Is.Empty);
        }

        Assert.That(CookedBinaryFormatterRegistry.TryCreateCollection(type, out _), Is.False);
        Assert.That(CookedBinaryFormatterRegistry.TryResolve(type.FullName!, out _), Is.False);
    }

    [Test]
    public void GeneratedHashSet_AddsUniqueValuesAndReleasesRegistration()
    {
        Assert.That(CookedBinaryFormatterRegistry.TryCreateHashSet(typeof(string), 2, out _, out _), Is.False);
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(CookedBinaryFormatterRegistry.TryCreateHashSet(typeof(string), 2, out object? set, out Action<object, object?>? add), Is.True);
            add!(set!, "entry");
            add(set!, "entry");
            Assert.That(set, Is.TypeOf<HashSet<string>>());
            Assert.That((HashSet<string>)set!, Is.EquivalentTo(new[] { "entry" }));
        }

        Assert.That(CookedBinaryFormatterRegistry.TryCreateHashSet(typeof(string), 2, out _, out _), Is.False);
        Assert.That(CookedBinaryFormatterRegistry.TryResolve(typeof(HashSet<string>).FullName!, out _), Is.False);
    }

    [Test]
    public void GeneratedNullable_RegistersUnderlyingTypeAndReleasesRegistration()
    {
        Assert.That(CookedBinaryFormatterRegistry.IsNullableRegistered(typeof(DateTime)), Is.False);
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(CookedBinaryFormatterRegistry.IsNullableRegistered(typeof(DateTime)), Is.True);
            Assert.That(CookedBinaryFormatterRegistry.TryResolve(typeof(DateTime?).FullName!, out Type? resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(typeof(DateTime?)));
        }

        Assert.That(CookedBinaryFormatterRegistry.IsNullableRegistered(typeof(DateTime)), Is.False);
        Assert.That(CookedBinaryFormatterRegistry.TryResolve(typeof(DateTime?).FullName!, out _), Is.False);
    }

    [Test]
    public void GeneratedTuple_ConstructsValuesAndLanguageDefault()
    {
        Type[] elementTypes = [typeof(int), typeof(int)];
        object?[] values = [3, 7];
        Assert.That(CookedBinaryFormatterRegistry.TryCreateTuple(elementTypes, values, out _), Is.False);
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(CookedBinaryFormatterRegistry.TryCreateTuple(elementTypes, values, out object? tuple), Is.True);
            Assert.That(tuple, Is.EqualTo((3, 7)));
            Assert.That(CookedBinaryFormatterRegistry.TryCreateValueDefault(typeof((int, int)), out object? defaultValue), Is.True);
            Assert.That(defaultValue, Is.EqualTo((0, 0)));
            byte[] payload = CookedBinarySerializer.Serialize((3, 7));
            Assert.That(CookedBinarySerializer.Deserialize(typeof((int, int)), payload), Is.EqualTo((3, 7)));
        }

        Assert.That(CookedBinaryFormatterRegistry.TryCreateTuple(elementTypes, values, out _), Is.False);
        Assert.That(CookedBinaryFormatterRegistry.TryCreateValueDefault(typeof((int, int)), out _), Is.False);
        Assert.That(CookedBinaryFormatterRegistry.TryResolve(typeof((int, int)).FullName!, out _), Is.False);
    }

    [Test]
    public void GeneratedArray_RoundTripsRuntimePayloadAndReleasesRegistration()
    {
        Assert.That(CookedBinaryFormatterRegistry.TryCreateArray(typeof(long[]), 2, out _), Is.False);
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(CookedBinaryFormatterRegistry.TryCreateArray(typeof(long[]), 2, out Array? array), Is.True);
            Assert.That(array, Is.TypeOf<long[]>());
            Assert.That(array!.Length, Is.EqualTo(2));
            long[] source = [1L, (long)int.MaxValue + 1];
            byte[] payload = RuntimeCookedBinarySerializer.Serialize(source);
            Assert.That(RuntimeCookedBinarySerializer.Deserialize(typeof(long[]), payload), Is.EqualTo(source));
        }

        Assert.That(CookedBinaryFormatterRegistry.TryCreateArray(typeof(long[]), 2, out _), Is.False);
        Assert.That(CookedBinaryFormatterRegistry.TryResolve(typeof(long[]).FullName!, out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NullableTimestamp_RoundTripsWithoutClosedFormatter(bool hasValue)
    {
        Assert.That(CookedBinaryFormatterRegistry.IsNullableRegistered(typeof(DateTime)), Is.False);
        DateTime? source = hasValue ? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) : null;

        // Both writers receive the boxed underlying value or null, not a Nullable wire wrapper.
        byte[] runtimePayload = RuntimeCookedBinarySerializer.Serialize(source);
        Assert.That(RuntimeCookedBinarySerializer.Deserialize(typeof(DateTime?), runtimePayload), Is.EqualTo(source));
        byte[] authoringPayload = CookedBinarySerializer.Serialize(source);
        Assert.That(CookedBinarySerializer.Deserialize(typeof(DateTime?), authoringPayload), Is.EqualTo(source));
    }

    [Test]
    public void PersistentEvent_RoundTripsCallsWithoutClosedListFormatter()
    {
        Assert.That(CookedBinaryFormatterRegistry.TryCreateCollection(typeof(List<XRPersistentCall>), out _), Is.False);
        XRPersistentCall call = new()
        {
            NodeId = new Guid("10000000-0000-0000-0000-000000000001"),
            TargetObjectId = new Guid("20000000-0000-0000-0000-000000000002"),
            MethodName = "HandleValue",
            ParameterTypeNames = [typeof(float).AssemblyQualifiedName!],
            UseTupleExpansion = true
        };
        XREvent source = new() { PersistentCalls = [call] };

        byte[] payload = CookedBinarySerializer.Serialize(source);
        XREvent restored = (XREvent)CookedBinarySerializer.Deserialize(typeof(XREvent), payload)!;

        Assert.That(restored.PersistentCalls, Has.Count.EqualTo(1));
        XRPersistentCall restoredCall = restored.PersistentCalls![0];
        Assert.Multiple(() =>
        {
            Assert.That(restoredCall.NodeId, Is.EqualTo(call.NodeId));
            Assert.That(restoredCall.TargetObjectId, Is.EqualTo(call.TargetObjectId));
            Assert.That(restoredCall.MethodName, Is.EqualTo(call.MethodName));
            Assert.That(restoredCall.ParameterTypeNames, Is.EqualTo(call.ParameterTypeNames));
            Assert.That(restoredCall.UseTupleExpansion, Is.True);
        });
    }
}
