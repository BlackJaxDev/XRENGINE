using System.Text.Json;
using XREngine.Editor.MaterialAuthoring;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

internal enum EInspectorHarnessInput
{
    Keyboard,
    Mouse,
    DragDrop,
    Clipboard,
    Reset,
    AnimationMode,
    ContextAction,
}

internal sealed class SourceToonInspectorInteractionHarness
{
    private readonly ShaderAuthoringSchema _schema;
    private readonly Dictionary<string, string> _localizedLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly List<string> _interactionLog = [];

    public SourceToonInspectorInteractionHarness(ShaderAuthoringSchema schema, XRMaterial material)
    {
        _schema = schema;
        Material = material;
    }

    public XRMaterial Material { get; }
    public string? SelectedSemanticId { get; private set; }
    public string? FocusedSemanticId { get; private set; }
    public string Filter { get; private set; } = string.Empty;
    public string Locale { get; private set; } = "en";
    public bool PreviewActive { get; private set; }
    public bool ModalActive { get; private set; }
    public bool BackgroundWorkActive { get; private set; }
    public bool ViewportToolActive { get; private set; }
    public IReadOnlySet<string> Expanded => _expanded;
    public IReadOnlyList<string> InteractionLog => _interactionLog;

    public bool Select(string semanticId, EInspectorHarnessInput input = EInspectorHarnessInput.Mouse)
    {
        if (!_schema.NodeLookup.TryGetValue(semanticId, out ShaderAuthoringNode? node))
            return false;
        SelectedSemanticId = semanticId;
        FocusedSemanticId = semanticId;
        foreach (ShaderAuthoringNode ancestor in node.Ancestors())
            _expanded.Add(ancestor.SemanticId);
        _interactionLog.Add($"{input}:select:{semanticId}");
        return true;
    }

    public void SetExpanded(string semanticId, bool expanded)
    {
        if (expanded)
            _expanded.Add(semanticId);
        else
            _expanded.Remove(semanticId);
        _interactionLog.Add($"expand:{semanticId}:{expanded}");
    }

    public void SetLocale(string locale, IReadOnlyDictionary<string, string>? labels = null)
    {
        Locale = locale;
        _localizedLabels.Clear();
        if (labels is not null)
            foreach ((string key, string value) in labels)
                _localizedLabels[key] = value;
        _interactionLog.Add($"locale:{locale}");
    }

    public IReadOnlyList<ShaderAuthoringNode> Search(string filter)
    {
        Filter = filter ?? string.Empty;
        if (Filter.Length == 0)
            return _schema.DeclarationOrder;

        List<ShaderAuthoringNode> matches = [];
        foreach (ShaderAuthoringNode node in _schema.DeclarationOrder)
        {
            string localized = _localizedLabels.GetValueOrDefault(node.SemanticId) ?? string.Empty;
            if (!node.DisplayName.Contains(Filter, StringComparison.OrdinalIgnoreCase) &&
                !(node.SourcePropertyName?.Contains(Filter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !localized.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(node);
            foreach (ShaderAuthoringNode ancestor in node.Ancestors())
                _expanded.Add(ancestor.SemanticId);
        }
        return matches;
    }

    public bool EditFloat(string propertyName, float value, EInspectorHarnessInput input)
    {
        ShaderFloat? parameter = Material.Parameter<ShaderFloat>(propertyName);
        if (parameter is null)
            return false;
        MaterialAuthoringTransaction transaction = new($"Harness edit {propertyName}");
        transaction.Add(
            Material,
            propertyName,
            () => parameter.Value = value,
            invalidatesVariant: true);
        bool succeeded = transaction.TryExecute(out _);
        if (succeeded)
            _interactionLog.Add($"{input}:edit:{propertyName}:{value:R}");
        return succeeded;
    }

    public bool ResetFloat(string propertyName, float value)
        => EditFloat(propertyName, value, EInspectorHarnessInput.Reset);

    public bool SetAnimationMode(string propertyName, EShaderUiPropertyMode mode)
    {
        bool changed = Material.SetUberPropertyMode(propertyName, mode);
        _interactionLog.Add($"animation:{propertyName}:{mode}");
        return changed;
    }

    public void BeginPreview()
    {
        PreviewActive = true;
        _interactionLog.Add("preview:begin");
    }

    public void BeginModal()
    {
        ModalActive = true;
        _interactionLog.Add("modal:begin");
    }

    public void BeginBackgroundWork()
    {
        BackgroundWorkActive = true;
        _interactionLog.Add("background:begin");
    }

    public void BeginViewportTool()
    {
        ViewportToolActive = true;
        _interactionLog.Add("viewport:begin");
    }

    public void CancelTransientWork()
    {
        PreviewActive = false;
        ModalActive = false;
        BackgroundWorkActive = false;
        ViewportToolActive = false;
        _interactionLog.Add("transient:cancel");
    }

    public void OnSelectionOrRendererChanged()
        => CancelTransientWork();

    public void ReimportSchema(ShaderAuthoringSchema schema)
    {
        if (SelectedSemanticId is not null && !schema.NodeLookup.ContainsKey(SelectedSemanticId))
        {
            SelectedSemanticId = null;
            FocusedSemanticId = null;
        }
        _interactionLog.Add("schema:reimport");
    }

    public string SavePersistentState()
        => JsonSerializer.Serialize(new PersistentState(
            1,
            Locale,
            _expanded.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            SelectedSemanticId));

    public void RestorePersistentState(string json)
    {
        PersistentState? state = JsonSerializer.Deserialize<PersistentState>(json);
        if (state?.Version != 1)
            return;
        Locale = state.Locale;
        _expanded.Clear();
        foreach (string semanticId in state.Expanded)
            if (_schema.NodeLookup.ContainsKey(semanticId))
                _expanded.Add(semanticId);
        if (state.SelectedSemanticId is not null && _schema.NodeLookup.ContainsKey(state.SelectedSemanticId))
            SelectedSemanticId = state.SelectedSemanticId;
        PreviewActive = false;
        ModalActive = false;
        BackgroundWorkActive = false;
        ViewportToolActive = false;
    }

    private sealed record PersistentState(
        int Version,
        string Locale,
        string[] Expanded,
        string? SelectedSemanticId);
}
