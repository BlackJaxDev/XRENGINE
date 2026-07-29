namespace XREngine.Rendering;

/// <summary>
/// Suppresses redundant global table binds within one compatible command scope.
/// </summary>
public sealed class AdvancedGlobalResourceTableBinder
{
    private bool _hasBinding;
    private ulong _commandScopeId;
    private AdvancedGlobalResourceTableSet _tables;
    private IAdvancedGlobalResourceTableBindingBackend? _bindingBackend;

    public bool BindOnce(
        IAdvancedGlobalResourceTableBindingBackend backend,
        ulong commandScopeId,
        in AdvancedGlobalResourceTableSet tables)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (_hasBinding &&
            _commandScopeId == commandScopeId &&
            _tables == tables &&
            ReferenceEquals(_bindingBackend, backend))
        {
            return false;
        }

        backend.BindGlobalResourceTables(tables);
        _hasBinding = true;
        _commandScopeId = commandScopeId;
        _tables = tables;
        _bindingBackend = backend;
        return true;
    }

    public void Invalidate()
    {
        _hasBinding = false;
        _commandScopeId = 0ul;
        _tables = default;
        _bindingBackend = null;
    }
}
