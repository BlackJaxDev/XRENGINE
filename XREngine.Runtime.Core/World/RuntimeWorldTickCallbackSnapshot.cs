using XREngine.Components;

namespace XREngine;

/// <summary>
/// Cumulative cost of one kind of tick callback, named by the method that runs,
/// the type it runs on and, for a compiler-generated closure, the method the
/// closure forwards to. Tick counts and timestamps use the stopwatch frequency
/// reported by the enclosing <see cref="RuntimeWorldTickTelemetrySnapshot"/>.
/// </summary>
/// <param name="DeclaringType">Type that declares the invoked method.</param>
/// <param name="Method">Invoked method.</param>
/// <param name="TargetType">Runtime type of the object the method runs on; empty for static methods.</param>
/// <param name="InnerDeclaringType">Declaring type of the method a closure forwards to; empty when none.</param>
/// <param name="InnerMethod">Method a closure forwards to; empty when none.</param>
/// <param name="Group">Tick group of the queue.</param>
/// <param name="Order">Order value of the queue within its group.</param>
/// <param name="RegisteredInstances">Callbacks of this kind registered now.</param>
/// <param name="RegisteredInstancesHighWater">Most callbacks of this kind registered at once.</param>
/// <param name="Invocations">Invocations since launch.</param>
/// <param name="Ticks">Total stopwatch ticks spent in the callback.</param>
/// <param name="MaximumTicks">Longest single invocation.</param>
/// <param name="MaximumTimestamp">Stopwatch timestamp at which the longest invocation started.</param>
/// <param name="AllocatedBytes">Managed bytes allocated by the dispatching thread during invocations.</param>
/// <param name="AtLeastQuarterMillisecond">Invocations of 0.25 ms or longer.</param>
/// <param name="AtLeastOneMillisecond">Invocations of 1 ms or longer.</param>
/// <param name="AtLeastFourMilliseconds">Invocations of 4 ms or longer.</param>
/// <param name="AtLeastSixteenMilliseconds">Invocations of 16 ms or longer.</param>
/// <param name="AtLeastOneMillisecondWithCollection">Invocations of 1 ms or longer during which a garbage collection completed.</param>
public readonly record struct RuntimeWorldTickCallbackSnapshot(
    string DeclaringType,
    string Method,
    string TargetType,
    string InnerDeclaringType,
    string InnerMethod,
    ETickGroup Group,
    int Order,
    long RegisteredInstances,
    long RegisteredInstancesHighWater,
    long Invocations,
    long Ticks,
    long MaximumTicks,
    long MaximumTimestamp,
    long AllocatedBytes,
    long AtLeastQuarterMillisecond,
    long AtLeastOneMillisecond,
    long AtLeastFourMilliseconds,
    long AtLeastSixteenMilliseconds,
    long AtLeastOneMillisecondWithCollection);
