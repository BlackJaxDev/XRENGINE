using System.Reflection;
using System.Runtime.CompilerServices;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static readonly ConditionalWeakTable<object, InspectorTargetSet> _singleInspectorTargets = new();

    /// <summary>Retains inspector state only for as long as the inspected object is alive.</summary>
    internal static InspectorTargetSet GetInspectorTargets(object target)
        => _singleInspectorTargets.GetValue(target, static value => new InspectorTargetSet(value));

    /// <summary>
    /// An immutable selection with member-local value storage. Buffers cannot be shared between
    /// rows: nested inspectors and deferred file dialogs may still refer to an earlier member.
    /// </summary>
    public sealed class InspectorTargetSet
    {
        private readonly Dictionary<MemberInfo, InspectorValueBuffer> _memberValues = new();

        internal InspectorTargetSet(object target)
        {
            Targets = new[] { target };
            CommonType = target.GetType();
        }

        public InspectorTargetSet(IReadOnlyList<object> targets, Type commonType)
        {
            ArgumentNullException.ThrowIfNull(targets);
            ArgumentNullException.ThrowIfNull(commonType);
            if (targets.Count == 0)
                throw new ArgumentException("Inspector target list must not be empty.", nameof(targets));

            // Snapshot scratch lists so a dialog opened for one selection never edits the next.
            var snapshot = new object[targets.Count];
            for (int i = 0; i < targets.Count; i++)
                snapshot[i] = targets[i];
            Targets = snapshot;
            CommonType = commonType;
        }

        public IReadOnlyList<object> Targets { get; }
        public Type CommonType { get; }
        public object PrimaryTarget => Targets[0];
        public bool HasMultipleTargets => Targets.Count > 1;

        internal InspectorValueBuffer GetValues(MemberInfo member)
        {
            if (!_memberValues.TryGetValue(member, out InspectorValueBuffer? values))
                _memberValues.Add(member, values = new InspectorValueBuffer(Targets.Count));
            return values;
        }

        /// <summary>Checks identity without allocating or retaining a mutable selection list.</summary>
        internal bool Matches(IReadOnlyList<object> targets, Type commonType)
        {
            if (CommonType != commonType || Targets.Count != targets.Count)
                return false;
            for (int i = 0; i < targets.Count; i++)
                if (!ReferenceEquals(Targets[i], targets[i]))
                    return false;
            return true;
        }
    }
}
