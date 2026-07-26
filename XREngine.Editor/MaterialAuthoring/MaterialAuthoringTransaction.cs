using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Validates a collection of material mutations before applying them as one
/// undo entry. Variant invalidation is coalesced to one request per material.
/// </summary>
public sealed class MaterialAuthoringTransaction
{
    private readonly List<Step> _steps = [];
    private readonly HashSet<XRMaterial> _targets =
        new(ReferenceEqualityComparer.Instance);

    public MaterialAuthoringTransaction(string description)
        => Description = string.IsNullOrWhiteSpace(description)
            ? "Edit Material"
            : description.Trim();

    public string Description { get; }

    public MaterialAuthoringTransaction Add(
        XRMaterial material,
        string description,
        Func<string?> validate,
        Action apply,
        bool invalidatesVariant = false)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(apply);
        _targets.Add(material);
        _steps.Add(new(material, description, validate, apply, null, invalidatesVariant));
        return this;
    }

    public MaterialAuthoringTransaction Add(
        XRMaterial material,
        string description,
        Action apply,
        bool invalidatesVariant = false)
        => Add(material, description, static () => null, apply, invalidatesVariant);

    public MaterialAuthoringTransaction AddStructural(
        XRMaterial material,
        string description,
        Func<string?> validate,
        Action apply,
        Action undo,
        bool invalidatesVariant = false)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(undo);
        _targets.Add(material);
        _steps.Add(new(material, description, validate, apply, undo, invalidatesVariant));
        return this;
    }

    public MaterialAuthoringTransaction AddStructural(
        XRMaterial material,
        string description,
        Action apply,
        Action undo,
        bool invalidatesVariant = false)
        => AddStructural(material, description, static () => null, apply, undo, invalidatesVariant);

    public bool TryExecute(out MaterialAuthoringTransactionReport report)
    {
        List<string> diagnostics = [];
        foreach (Step step in _steps)
        {
            string? diagnostic;
            try
            {
                diagnostic = step.Validate();
            }
            catch (Exception exception)
            {
                diagnostic = exception.GetBaseException().Message;
            }

            if (!string.IsNullOrWhiteSpace(diagnostic))
                diagnostics.Add($"{step.Description}: {diagnostic}");
        }

        if (diagnostics.Count > 0)
        {
            report = new(false, 0, diagnostics);
            return false;
        }

        Exception? applyFailure = null;
        using (IDisposable interaction = Undo.BeginUserInteraction())
        using (Undo.ChangeScope change = Undo.BeginChange(Description))
        {
            foreach (XRMaterial target in _targets)
                Undo.Track(target);

            int appliedSteps = 0;
            try
            {
                foreach (Step step in _steps)
                {
                    step.Apply();
                    appliedSteps++;
                }
                foreach (Step step in _steps)
                    if (step.Undo is not null)
                        Undo.RecordStructuralChange(step.Description, step.Undo, step.Apply);
            }
            catch (Exception exception)
            {
                applyFailure = exception;
                for (int index = appliedSteps - 1; index >= 0; index--)
                {
                    try
                    {
                        _steps[index].Undo?.Invoke();
                    }
                    catch
                    {
                        // Preserve the original apply failure; Undo.TryUndo below
                        // restores tracked XRBase state.
                    }
                }
            }
        }

        if (applyFailure is not null)
        {
            diagnostics.Add(applyFailure.GetBaseException().Message);
            Undo.TryUndo();
            report = new(false, 0, diagnostics);
            return false;
        }

        foreach (XRMaterial target in _targets)
        {
            bool needsVariant = false;
            foreach (Step step in _steps)
            {
                if (ReferenceEquals(step.Material, target) && step.InvalidatesVariant)
                {
                    needsVariant = true;
                    break;
                }
            }

            if (needsVariant)
                target.RequestUberVariantRebuild();
            target.MarkDirty();
        }

        report = new(true, _steps.Count, diagnostics);
        return true;
    }

    private sealed record Step(
        XRMaterial Material,
        string Description,
        Func<string?> Validate,
        Action Apply,
        Action? Undo,
        bool InvalidatesVariant);
}

public sealed record MaterialAuthoringTransactionReport(
    bool Succeeded,
    int AppliedStepCount,
    IReadOnlyList<string> Diagnostics);
