using System.Globalization;

namespace XREngine;

/// <summary>
/// Editor-facing metadata for one environment variable recognized by XREngine.
/// </summary>
public sealed record RuntimeEnvironmentVariableDescriptor(
    string FieldName,
    string Name,
    RuntimeEnvironmentCategory Category,
    RuntimeEnvironmentValueKind ValueKind,
    RuntimeEnvironmentApplyMode ApplyMode,
    bool IsDiagnosticOrValidation,
    bool IsDowngradeOverride,
    string DefaultBehavior,
    IReadOnlyList<string>? AllowedValues = null,
    IReadOnlyList<string>? AcceptedAliases = null,
    bool AcceptsNumericEnumValues = false)
{
    private static readonly string[] EmptyValues = [];

    /// <summary>
    /// Canonical selectable values for enum or fixed-choice variables. Empty for free-form variables.
    /// </summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = AllowedValues ?? EmptyValues;

    /// <summary>
    /// Additional string aliases accepted by the variable's parser besides <see cref="AllowedValues"/>.
    /// </summary>
    public IReadOnlyList<string> AcceptedAliases { get; init; } = AcceptedAliases ?? EmptyValues;

    /// <summary>
    /// Returns true when <paramref name="value"/> is unset or matches the supported syntax
    /// and allowed values of this variable.
    /// </summary>
    public bool IsValueSupported(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        string trimmed = value.Trim();

        if (AllowedValues.Count > 0)
        {
            for (int i = 0; i < AllowedValues.Count; i++)
                if (trimmed.Equals(AllowedValues[i], StringComparison.OrdinalIgnoreCase))
                    return true;

            for (int i = 0; i < AcceptedAliases.Count; i++)
                if (trimmed.Equals(AcceptedAliases[i], StringComparison.OrdinalIgnoreCase))
                    return true;

            if (AcceptsNumericEnumValues &&
                int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericValue) &&
                numericValue >= 0 &&
                numericValue < AllowedValues.Count)
            {
                return true;
            }

            return false;
        }

        return ValueKind switch
        {
            RuntimeEnvironmentValueKind.Boolean => IsSupportedBooleanToken(trimmed),
            RuntimeEnvironmentValueKind.Integer => long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            RuntimeEnvironmentValueKind.Number => double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed),
            _ => true,
        };
    }

    /// <summary>
    /// Returns a human-readable summary of valid values for tooltips and error messages.
    /// </summary>
    public string FormatSupportedValuesSummary()
    {
        if (AllowedValues.Count > 0)
            return string.Join(", ", AllowedValues);

        return ValueKind switch
        {
            RuntimeEnvironmentValueKind.Boolean => "1, 0, true, false, yes, no, on, off",
            RuntimeEnvironmentValueKind.Integer => "Any whole integer",
            RuntimeEnvironmentValueKind.Number => "Any finite decimal number",
            _ => "Any text value",
        };
    }

    private static bool IsSupportedBooleanToken(string trimmed)
        => trimmed is "1" or "0" ||
           trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("no", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("off", StringComparison.OrdinalIgnoreCase);
}
