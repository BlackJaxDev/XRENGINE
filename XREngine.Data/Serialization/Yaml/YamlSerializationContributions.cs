using YamlDotNet.Serialization;

namespace XREngine.Serialization;

/// <summary>
/// Contributes feature-owned YAML converters and builder customization without introducing an
/// upward project reference in the asset runtime.
/// </summary>
public interface IYamlSerializationContribution
{
    string OwnerName { get; }

    int Priority => 0;

    IEnumerable<IYamlTypeConverter> CreateTypeConverters()
        => [];

    void ConfigureSerializer(SerializerBuilder builder)
    {
    }

    void ConfigureDeserializer(DeserializerBuilder builder)
    {
    }
}

/// <summary>Lease-based registry for explicitly installed YAML feature owners.</summary>
public static class YamlSerializationContributions
{
    private static readonly object Sync = new();
    private static Registration[] _registrations = [];
    private static long _version;
    private static long _nextSequence;

    public static long Version => Volatile.Read(ref _version);

    public static IDisposable Install(IYamlSerializationContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (string.IsNullOrWhiteSpace(contribution.OwnerName))
            throw new ArgumentException("A YAML serialization contribution must declare its owner name.", nameof(contribution));

        Registration registration;
        lock (Sync)
        {
            if (Array.Exists(_registrations, item => ReferenceEquals(item.Contribution, contribution)))
                throw new InvalidOperationException($"The YAML contribution '{contribution.OwnerName}' is already installed.");

            registration = new Registration(contribution, ++_nextSequence);
            Registration[] updated = [.. _registrations, registration];
            Array.Sort(updated, static (left, right) =>
            {
                int priority = left.Contribution.Priority.CompareTo(right.Contribution.Priority);
                return priority != 0 ? priority : left.Sequence.CompareTo(right.Sequence);
            });
            Volatile.Write(ref _registrations, updated);
            Interlocked.Increment(ref _version);
        }

        return new ContributionLease(registration);
    }

    public static IReadOnlyList<IYamlSerializationContribution> Snapshot()
        => [.. Volatile.Read(ref _registrations).Select(static item => item.Contribution)];

    private sealed record Registration(IYamlSerializationContribution Contribution, long Sequence);

    private sealed class ContributionLease(Registration registration) : IDisposable
    {
        private Registration? _registration = registration;

        public void Dispose()
        {
            Registration? current = Interlocked.Exchange(ref _registration, null);
            if (current is null)
                return;

            lock (Sync)
            {
                int index = Array.FindIndex(_registrations, item => ReferenceEquals(item, current));
                if (index < 0)
                    return;

                Registration[] updated = new Registration[_registrations.Length - 1];
                if (index > 0)
                    Array.Copy(_registrations, 0, updated, 0, index);
                if (index < _registrations.Length - 1)
                    Array.Copy(_registrations, index + 1, updated, index, _registrations.Length - index - 1);
                Volatile.Write(ref _registrations, updated);
                Interlocked.Increment(ref _version);
            }
        }
    }
}
