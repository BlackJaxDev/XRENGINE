using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public sealed class MaterialLinkGroup
{
    private bool _propagating;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Material Link";
    public string SemanticPropertyId { get; init; } = string.Empty;
    public List<XRMaterial> Members { get; } = [];

    public bool TryAdd(XRMaterial material, out string? diagnostic)
    {
        diagnostic = null;
        if (Members.Contains(material, ReferenceEqualityComparer.Instance))
        {
            diagnostic = "The material is already linked.";
            return false;
        }
        Members.Add(material);
        return true;
    }

    public bool Propagate(
        XRMaterial source,
        Action<XRMaterial, XRMaterial> copyValue,
        out MaterialAuthoringTransactionReport report)
    {
        if (_propagating)
        {
            report = new(false, 0, ["A material-link propagation cycle was prevented."]);
            return false;
        }

        _propagating = true;
        try
        {
            MaterialAuthoringTransaction transaction = new($"Propagate {SemanticPropertyId}");
            foreach (XRMaterial member in Members)
            {
                if (ReferenceEquals(member, source))
                    continue;
                XRMaterial target = member;
                transaction.Add(target, SemanticPropertyId, () => copyValue(source, target), true);
            }
            return transaction.TryExecute(out report);
        }
        finally
        {
            _propagating = false;
        }
    }
}

public sealed class DecalPositioningSession : IDisposable
{
    private readonly XRMaterial _material;
    private readonly Action<XRMaterial, DecalTransform> _applyPreview;
    private readonly DecalTransform _before;
    private bool _committed;
    private bool _disposed;

    public DecalPositioningSession(
        XRMaterial material,
        int materialSlot,
        DecalTransform initial,
        Action<XRMaterial, DecalTransform> applyPreview)
    {
        _material = material;
        MaterialSlot = materialSlot;
        Current = _before = initial;
        _applyPreview = applyPreview;
    }

    public int MaterialSlot { get; }
    public DecalTransform Current { get; private set; }

    public void Preview(DecalTransform transform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Current = transform;
        _applyPreview(_material, transform);
    }

    public bool Commit(out MaterialAuthoringTransactionReport report)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DecalTransform final = Current;
        _applyPreview(_material, _before);
        MaterialAuthoringTransaction transaction = new("Position Material Decal");
        transaction.Add(_material, "Decal transform", () => _applyPreview(_material, final), true);
        _committed = transaction.TryExecute(out report);
        return _committed;
    }

    public void Cancel()
    {
        if (_disposed || _committed)
            return;
        _applyPreview(_material, _before);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Cancel();
        _disposed = true;
    }
}

public readonly record struct DecalTransform(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    Vector2 UvOffset,
    Vector2 UvScale,
    float DepthOffset,
    bool Mirrored);

public sealed class MaterialAuthoringLocaleCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> _locales =
        new(StringComparer.OrdinalIgnoreCase);

    public string FallbackLocale { get; init; } = "en";

    public void Add(string locale, IReadOnlyDictionary<string, string> values)
        => _locales[locale] = new(values, StringComparer.Ordinal);

    public string Resolve(
        string? locale,
        string semanticId,
        string sourceFallback,
        params object?[] arguments)
    {
        string? value = null;
        if (locale is not null && _locales.TryGetValue(locale, out Dictionary<string, string>? selected))
            selected.TryGetValue(semanticId, out value);
        if (value is null && _locales.TryGetValue(FallbackLocale, out Dictionary<string, string>? fallback))
            fallback.TryGetValue(semanticId, out value);
        value ??= sourceFallback;
        return arguments.Length == 0
            ? value
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, value, arguments);
    }
}

public sealed class MaterialAuthoringTelemetry
{
    private long _buildTicks;
    private long _drawTicks;
    private int _visibleNodes;
    private int _submittedNodes;
    private int _conditionInvalidations;
    private int _variantRequests;

    public static MaterialAuthoringTelemetry Instance { get; } = new();

    public void RecordBuild(TimeSpan elapsed, int visibleNodes)
    {
        Interlocked.Exchange(ref _buildTicks, elapsed.Ticks);
        Interlocked.Exchange(ref _visibleNodes, visibleNodes);
    }

    public void RecordDraw(TimeSpan elapsed, int submittedNodes)
    {
        Interlocked.Exchange(ref _drawTicks, elapsed.Ticks);
        Interlocked.Exchange(ref _submittedNodes, submittedNodes);
    }

    public void RecordConditionInvalidation() => Interlocked.Increment(ref _conditionInvalidations);
    public void RecordVariantRequest() => Interlocked.Increment(ref _variantRequests);

    public MaterialAuthoringTelemetrySnapshot Snapshot()
        => new(
            TimeSpan.FromTicks(Interlocked.Read(ref _buildTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref _drawTicks)),
            Volatile.Read(ref _visibleNodes),
            Volatile.Read(ref _submittedNodes),
            Volatile.Read(ref _conditionInvalidations),
            Volatile.Read(ref _variantRequests));
}

public readonly record struct MaterialAuthoringTelemetrySnapshot(
    TimeSpan BuildTime,
    TimeSpan DrawTime,
    int VisibleNodes,
    int SubmittedNodes,
    int ConditionInvalidations,
    int VariantRequests);
