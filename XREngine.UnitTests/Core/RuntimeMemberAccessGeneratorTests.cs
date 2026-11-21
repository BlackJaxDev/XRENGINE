using NUnit.Framework;

namespace XREngine.UnitTests.Core;

/// <summary>Checks compiled generated accessors and their registration lifetime.</summary>
[TestFixture]
[NonParallelizable]
public sealed class RuntimeMemberAccessGeneratorTests
{
    [Test]
    public void GeneratedProperty_ReadsAndWritesUntilLeaseDisposal()
    {
        RuntimeMemberAccessTarget target = new() { Value = 2.0f };
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(RuntimeMemberAccessorRegistry.TryGet(target, nameof(target.Value), out object? value), Is.True);
            Assert.That(value, Is.EqualTo(2.0f));
            Assert.That(RuntimeMemberAccessorRegistry.TrySet(target, nameof(target.Value), 3.0f), Is.True);
            Assert.That(target.Value, Is.EqualTo(3.0f));
        }

        Assert.That(RuntimeMemberAccessorRegistry.TryGet(target, nameof(target.Value), out _), Is.False);
        Assert.That(RuntimeMemberAccessorRegistry.TrySet(target, nameof(target.Value), 4.0f), Is.False);
        Assert.That(target.Value, Is.EqualTo(3.0f));
    }

    [Test]
    public void GeneratedField_PreservesReferencesAndAcceptsNull()
    {
        RuntimeMemberAccessTarget target = new();
        RuntimeMemberAccessTarget referencedTarget = new();
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(RuntimeMemberAccessorRegistry.TrySet(target, nameof(target.Target), referencedTarget), Is.True);
            Assert.That(target.Target, Is.SameAs(referencedTarget));
            Assert.That(RuntimeMemberAccessorRegistry.TryGet(target, nameof(target.Target), out object? value), Is.True);
            Assert.That(value, Is.SameAs(referencedTarget));
            Assert.That(RuntimeMemberAccessorRegistry.TrySet(target, nameof(target.Target), null), Is.True);
            Assert.That(target.Target, Is.Null);
        }

        Assert.That(RuntimeMemberAccessorRegistry.TryGet(target, nameof(target.Target), out _), Is.False);
        Assert.That(RuntimeMemberAccessorRegistry.TrySet(target, nameof(target.Target), referencedTarget), Is.False);
    }

    [Test]
    public void GeneratedMethod_RequiresMatchingParametersAndReleasesInvoker()
    {
        RuntimeMemberAccessTarget target = new();
        Type[] parameterTypes = [typeof(float)];
        object?[] arguments = [5.0f];
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(RuntimeMemberAccessorRegistry.TryInvoke(target, nameof(target.SetValue), [typeof(int)], arguments), Is.False);
            Assert.That(RuntimeMemberAccessorRegistry.TryInvoke(target, nameof(target.SetValue), parameterTypes, []), Is.False);
            Assert.That(target.Value, Is.Zero);
            Assert.That(RuntimeMemberAccessorRegistry.TryInvoke(target, nameof(target.SetValue), parameterTypes, arguments), Is.True);
            Assert.That(target.Value, Is.EqualTo(5.0f));
        }

        Assert.That(RuntimeMemberAccessorRegistry.TryInvoke(target, nameof(target.SetValue), parameterTypes, arguments), Is.False);
    }

    [Test]
    public void GeneratedEvent_SubscribesAndUnsubscribesHandler()
    {
        RuntimeMemberAccessTarget target = new();
        int calls = 0;
        Action handler = () => calls++;
        using (GeneratedRuntimeContractTestRegistration.Install())
        {
            Assert.That(RuntimeMemberAccessorRegistry.TrySubscribe(target, nameof(target.Changed), handler, true), Is.True);
            target.RaiseChanged();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(RuntimeMemberAccessorRegistry.TrySubscribe(target, nameof(target.Changed), handler, false), Is.True);
            target.RaiseChanged();
            Assert.That(calls, Is.EqualTo(1));
        }

        Assert.That(RuntimeMemberAccessorRegistry.TrySubscribe(target, nameof(target.Changed), handler, true), Is.False);
    }
}
