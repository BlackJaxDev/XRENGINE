using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>Retains one inspector row's exact targets until its binding is retired.</summary>
internal sealed class InspectorPropertyDropBinding
{
    private readonly Func<bool> _contextIsCurrent;
    private readonly EditorDropObjectIdentity[] _identities;
    private int _retired;

    public InspectorPropertyDropBinding(PropertyInfo property, object?[] targets,
        Func<bool> contextIsCurrent)
    {
        Property = property;
        Targets = targets.Where(target => target is not null).ToArray();
        _contextIsCurrent = contextIsCurrent;
        _identities = new EditorDropObjectIdentity[Targets.Length];
        for (int index = 0; index < Targets.Length; index++)
        {
            object target = Targets[index]!;
            _identities[index] = new(target);
        }
    }

    public PropertyInfo Property { get; }
    public object?[] Targets { get; }
    public void Retire() => Interlocked.Exchange(ref _retired, 1);

    public bool IsCurrent
    {
        get
        {
            if (Targets.Length == 0 || Volatile.Read(ref _retired) != 0 || !_contextIsCurrent())
                return false;
            foreach (EditorDropObjectIdentity identity in _identities)
                if (!identity.IsCurrent)
                    return false;
            return Volatile.Read(ref _retired) == 0 && _contextIsCurrent();
        }
    }

    public void RequireCurrent()
    {
        if (!IsCurrent)
            throw new InvalidOperationException("The inspector drop owner changed before assignment.");
    }
}
