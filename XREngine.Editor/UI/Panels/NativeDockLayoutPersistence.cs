using System.IO;
using System.Text;
using System.Text.Json;

namespace XREngine.Editor;

/// <summary>Preserves saved layout records and exact primary-save admission.</summary>
internal sealed class NativeDockLayoutPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string PrimaryWindowKey = NativeDockLayoutKeys.Primary;
    private static readonly string DefaultLegacyPrimaryKey = NativeDockLayoutKeys.LegacyPrimary(NativeDockLayoutKeys.DefaultEditorTitle);
    private string? _primaryWriteSource;
    private bool _primaryWriteAllowed;
    private NativeDockLayout? _primaryWriteLayout;
    private readonly Func<string> _read;
    private readonly Action<string, string, Func<bool>> _write;
    private bool _saving;
    private object _primaryAdmissionIntent = new();
    private object? _primaryLoadIntent;

    internal NativeDockLayoutPersistence(Func<string> read, Action<string> write)
        : this(read, (_, updated, _) => write(updated)) { }

    internal NativeDockLayoutPersistence(Func<string> read, Action<string, string, Func<bool>> write)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    public NativeDockLayout Load(string windowKey, string? currentWindowTitle = null)
    {
        object? loadIntent = windowKey == PrimaryWindowKey ? new object() : null;
        if (loadIntent is not null) _primaryLoadIntent = loadIntent;
        string source = _read();
        void Admit(bool allowed, NativeDockLayout? candidate = null)
        {
            if (ReferenceEquals(_primaryLoadIntent, loadIntent))
                SetPrimaryWriteAdmission(allowed, source, candidate);
        }
        if (windowKey == PrimaryWindowKey)
            Admit(false);
        if (!TryReadLayouts(source, out JsonDocument? document, out JsonElement root))
        {
            return new NativeDockLayout().Sanitize();
        }

        using (document)
        {
            if (windowKey != PrimaryWindowKey)
                return GetLayout(root, windowKey, out NativeDockLayout? stored) == NativeDockLayoutEntryState.Valid
                    ? stored! : new NativeDockLayout().Sanitize();

            bool allowWrite = true;
            NativeDockLayoutEntryState primaryState = GetLayout(root, PrimaryWindowKey, out NativeDockLayout? layout);
            if (primaryState == NativeDockLayoutEntryState.Valid)
            {
                Admit(true, layout);
                return layout!;
            }
            if (primaryState == NativeDockLayoutEntryState.Invalid)
            {
                allowWrite = false;
                Debug.UI("[NativeDock] The saved primary layout is invalid. Its entry will not be overwritten automatically.");
            }

            string? currentLegacyKey = string.IsNullOrEmpty(currentWindowTitle) ? null : NativeDockLayoutKeys.LegacyPrimary(currentWindowTitle);
            NativeDockLayoutEntryState currentState = currentLegacyKey is null
                ? NativeDockLayoutEntryState.Missing
                : GetLayout(root, currentLegacyKey, out layout);
            if (currentState == NativeDockLayoutEntryState.Valid)
            {
                Admit(allowWrite, layout);
                return layout!;
            }
            if (currentState == NativeDockLayoutEntryState.Invalid)
            {
                allowWrite = false;
                Debug.UI("[NativeDock] The matching legacy primary layout is invalid. It will not be migrated automatically.");
            }

            int legacyPrimaryCount = CountLegacyPrimaryLayouts(root);
            NativeDockLayoutEntryState defaultState = GetLayout(root, DefaultLegacyPrimaryKey, out layout);
            if (defaultState == NativeDockLayoutEntryState.Valid)
            {
                Admit(allowWrite, layout);
                return layout!;
            }
            if (defaultState == NativeDockLayoutEntryState.Invalid)
            {
                allowWrite = false;
                Debug.UI("[NativeDock] The default legacy primary layout is invalid. It will not be migrated automatically.");
            }

            if (currentState != NativeDockLayoutEntryState.Valid && legacyPrimaryCount > 1)
            {
                Admit(false);
                Debug.UI("[NativeDock] Several legacy primary layouts exist. Select the layout to migrate explicitly.");
                return new NativeDockLayout().Sanitize();
            }

            if (legacyPrimaryCount > 0)
            {
                allowWrite = false;
                Debug.UI("[NativeDock] The saved primary layout does not match this window title. Select the layout to migrate explicitly.");
            }

            layout = new NativeDockLayout().Sanitize();
            Admit(allowWrite, layout);
            return layout;
        }
    }

    public bool HasLayout(string windowKey)
    {
        string source = _read();
        if (!TryReadLayouts(source, out JsonDocument? document, out JsonElement root))
            return false;
        using (document)
            return GetLayout(root, windowKey, out _) == NativeDockLayoutEntryState.Valid;
    }

    public void MigrateLegacyDetached(string windowKey, int fallbackPanel)
    {
        NativeDockLayout layout = Load(windowKey);
        layout.DockTree = NativeDockTree.FromLegacyDetached(layout, fallbackPanel);
        Save(windowKey, layout);
    }

    public bool Save(string windowKey, NativeDockLayout layout)
    {
        if (_saving)
            throw new InvalidOperationException("A native layout save is already in progress.");
        _saving = true;
        try { return SaveCurrent(windowKey, layout); }
        finally { _saving = false; }
    }

    private bool SaveCurrent(string windowKey, NativeDockLayout layout)
    {
        string source = _read();
        if (windowKey == PrimaryWindowKey &&
            (!_primaryWriteAllowed || !ReferenceEquals(_primaryWriteLayout, layout) ||
             !string.Equals(_primaryWriteSource, source, StringComparison.Ordinal)))
        {
            Debug.UI("[NativeDock] The primary layout save is not admitted by the current load. No saved entry was changed.");
            return false;
        }

        if (!TryReadLayouts(source, out JsonDocument? document, out JsonElement root))
        {
            Debug.UI("[NativeDock] Layout was not saved because the saved layout data is invalid.");
            return false;
        }

        string updated;
        using (document)
        using (MemoryStream output = new())
        {
            using (Utf8JsonWriter writer = new(output))
            {
                writer.WriteStartObject();
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        if (string.Equals(property.Name, windowKey, StringComparison.Ordinal))
                            continue;
                        writer.WritePropertyName(property.Name);
                        property.Value.WriteTo(writer);
                    }
                }
                writer.WritePropertyName(windowKey);
                WriteLayout(writer, root, windowKey, layout);
                writer.WriteEndObject();
            }
            updated = Encoding.UTF8.GetString(output.ToArray());
        }

        // Serialization can invoke diagnostics. Reject source or host changes from callbacks.
        if (!string.Equals(_read(), source, StringComparison.Ordinal) ||
            windowKey == PrimaryWindowKey && (!_primaryWriteAllowed ||
                !ReferenceEquals(_primaryWriteLayout, layout) ||
                !string.Equals(_primaryWriteSource, source, StringComparison.Ordinal)))
            throw new InvalidOperationException("The native layout source changed during save.");

        NativeDockLayout? admittedLayout = _primaryWriteLayout;
        bool advancePrimaryAdmission = _primaryWriteAllowed && admittedLayout is not null &&
            string.Equals(_primaryWriteSource, source, StringComparison.Ordinal) &&
            !windowKey.StartsWith("0:", StringComparison.Ordinal) &&
            (windowKey != PrimaryWindowKey || ReferenceEquals(admittedLayout, layout));
        if (advancePrimaryAdmission)
            SetPrimaryWriteAdmission(true, updated, admittedLayout);
        object intent = _primaryAdmissionIntent;

        try
        {
            _write(source, updated, () => windowKey != PrimaryWindowKey ||
                ReferenceEquals(_primaryAdmissionIntent, intent));
        }
        catch
        {
            if (advancePrimaryAdmission && _primaryWriteAllowed &&
                ReferenceEquals(_primaryWriteLayout, admittedLayout) &&
                string.Equals(_primaryWriteSource, updated, StringComparison.Ordinal))
                SetPrimaryWriteAdmission(false, _read());
            throw;
        }
        return true;
    }

    private static NativeDockLayoutEntryState GetLayout(JsonElement root, string windowKey,
        out NativeDockLayout? layout)
    {
        layout = null;
        if (root.ValueKind != JsonValueKind.Object)
            return NativeDockLayoutEntryState.Missing;

        JsonElement stored = default;
        bool found = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, windowKey, StringComparison.Ordinal))
                continue;
            if (found)
                return NativeDockLayoutEntryState.Invalid;
            stored = property.Value;
            found = true;
        }

        if (!found)
            return NativeDockLayoutEntryState.Missing;
        if (stored.ValueKind != JsonValueKind.Object)
            return NativeDockLayoutEntryState.Invalid;

        try
        {
            layout = stored.Deserialize<NativeDockLayout>(JsonOptions);
        }
        catch (JsonException)
        {
            return NativeDockLayoutEntryState.Invalid;
        }

        if (layout is null)
            return NativeDockLayoutEntryState.Invalid;

        try
        {
            // Validate the saved tree before layout sanitization can replace it with a fallback.
            layout.DockTree?.Sanitize();
            layout.Sanitize();
            return NativeDockLayoutEntryState.Valid;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            layout = null;
            return NativeDockLayoutEntryState.Invalid;
        }
    }

    private static int CountLegacyPrimaryLayouts(JsonElement root)
    {
        int count = 0;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in root.EnumerateObject())
                if (property.Name.StartsWith("0:", StringComparison.Ordinal))
                    count++;
        return count;
    }

    private static void WriteLayout(Utf8JsonWriter writer, JsonElement root,
        string windowKey, NativeDockLayout layout)
    {
        JsonElement updated = JsonSerializer.SerializeToElement(layout.Sanitize(), JsonOptions);
        HashSet<string> knownFields = new(StringComparer.OrdinalIgnoreCase);
        writer.WriteStartObject();
        foreach (JsonProperty property in updated.EnumerateObject())
        {
            knownFields.Add(property.Name);
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }

        if (TryGetRawLayout(root, windowKey, out JsonElement original))
            foreach (JsonProperty property in original.EnumerateObject())
            {
                if (knownFields.Contains(property.Name))
                    continue;
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
        writer.WriteEndObject();
    }

    private static bool TryGetRawLayout(JsonElement root, string windowKey, out JsonElement layout)
    {
        layout = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        bool found = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, windowKey, StringComparison.Ordinal))
                continue;
            if (found || property.Value.ValueKind != JsonValueKind.Object)
                return false;
            layout = property.Value;
            found = true;
        }
        return found;
    }

    private void SetPrimaryWriteAdmission(bool allowed, string source,
        NativeDockLayout? layout = null)
    {
        _primaryAdmissionIntent = new();
        _primaryWriteAllowed = allowed;
        _primaryWriteSource = source;
        _primaryWriteLayout = allowed ? layout : null;
    }

    private static bool TryReadLayouts(string source, out JsonDocument? document,
        out JsonElement root)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            document = null;
            root = default;
            return true;
        }
        try
        {
            document = JsonDocument.Parse(source);
            root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Debug.UI("[NativeDock] Saved layout data is not a JSON object.");
                document.Dispose();
                document = null;
                root = default;
                return false;
            }
            return true;
        }
        catch (JsonException exception)
        {
            Debug.UI($"[NativeDock] Saved layout is invalid: {exception.Message}");
            document = null;
            root = default;
            return false;
        }
    }
}
