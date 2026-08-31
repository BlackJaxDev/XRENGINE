using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Context-owned sink for native Vulkan validation callbacks. It retains only
/// bounded aggregate evidence and never references the renderer facade.
/// </summary>
internal sealed unsafe class VulkanValidationDiagnostics
{
    private const int MaximumMessages = 128;
    private const int MaximumDeviceAddressBindings = 128;
    private readonly object _summaryLock = new();
    private readonly Dictionary<string, MessageAggregate> _messages =
        new(StringComparer.Ordinal);
    private int _overflowCount;
    private long _errorCount;
    private long _warningCount;
    private long _suppressedWarningCount;
    private readonly object _deviceAddressBindingLock = new();
    private readonly VulkanValidationDeviceAddressBinding[] _deviceAddressBindings =
        new VulkanValidationDeviceAddressBinding[MaximumDeviceAddressBindings];
    private long _deviceAddressBindingSerial;
    private long _deviceAddressBindingDrainedSerial;

    public uint HandleDebugMessage(
        uint rawMessageSeverity,
        uint rawMessageTypes,
        nint rawCallbackData,
        in VulkanSubmissionDiagnosticContext submission)
    {
        _ = rawMessageTypes;
        DebugUtilsMessageSeverityFlagsEXT severity =
            (DebugUtilsMessageSeverityFlagsEXT)rawMessageSeverity;
        DebugUtilsMessengerCallbackDataEXT* callbackData =
            (DebugUtilsMessengerCallbackDataEXT*)rawCallbackData;
        if (callbackData is null)
            return Vk.False;

        RecordDeviceAddressBindings(callbackData);

        string message = Marshal.PtrToStringAnsi((nint)callbackData->PMessage) ?? "<null>";
        if (severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) &&
            message.Contains("this write is unused", StringComparison.Ordinal) &&
            message.Contains("pColorAttachments", StringComparison.Ordinal))
        {
            lock (_summaryLock)
            {
                _warningCount++;
                _suppressedWarningCount++;
            }
            return Vk.False;
        }

        string objectSummary = FormatObjects(callbackData);
        if (!string.IsNullOrEmpty(objectSummary))
            message = $"{message} objects=[{objectSummary}]";

        bool isError = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);
        Record(severity, callbackData, message, submission);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanValidationMessage(isError, message);

        if (isError)
            Debug.VulkanError($"[Vulkan] {message}");
        else if (severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt))
            Debug.VulkanWarning($"[Vulkan] {message}");
        else
            Debug.Vulkan($"[Vulkan] {message}");

        return Vk.False;
    }

    /// <summary>
    /// Drains the native callback payloads into cold-path managed storage for
    /// renderer-level resource correlation during device-loss reporting.
    /// </summary>
    public VulkanValidationDeviceAddressBinding[] DrainDeviceAddressBindings(
        out int overflowCount)
    {
        lock (_deviceAddressBindingLock)
        {
            long latestSerial = _deviceAddressBindingSerial;
            long firstRetainedSerial = Math.Max(
                _deviceAddressBindingDrainedSerial + 1,
                latestSerial - MaximumDeviceAddressBindings + 1);
            overflowCount = checked((int)Math.Min(
                Math.Max(0, firstRetainedSerial - (_deviceAddressBindingDrainedSerial + 1)),
                int.MaxValue));
            if (firstRetainedSerial > latestSerial)
            {
                return [];
            }

            int count = checked((int)(latestSerial - firstRetainedSerial + 1));
            VulkanValidationDeviceAddressBinding[] result = new VulkanValidationDeviceAddressBinding[count];
            for (int i = 0; i < count; i++)
            {
                long serial = firstRetainedSerial + i;
                int index = unchecked((int)((serial - 1) % MaximumDeviceAddressBindings));
                result[i] = _deviceAddressBindings[index];
            }
            _deviceAddressBindingDrainedSerial = latestSerial;
            return result;
        }
    }

    private void RecordDeviceAddressBindings(DebugUtilsMessengerCallbackDataEXT* callbackData)
        => RecordDeviceAddressBindings(
            (BaseInStructure*)callbackData->PNext,
            remainingNodes: 64);

    private void RecordDeviceAddressBindings(
        BaseInStructure* current,
        int remainingNodes)
    {
        if (current is null || remainingNodes <= 0)
            return;

        if (current->SType == StructureType.DeviceAddressBindingCallbackDataExt)
        {
            DeviceAddressBindingCallbackDataEXT* binding =
                (DeviceAddressBindingCallbackDataEXT*)current;
            if (binding->BaseAddress != 0 && binding->Size != 0)
            {
                lock (_deviceAddressBindingLock)
                {
                    long serial = ++_deviceAddressBindingSerial;
                    int index = unchecked((int)((serial - 1) % MaximumDeviceAddressBindings));
                    _deviceAddressBindings[index] = new(
                        binding->BaseAddress,
                        binding->Size,
                        binding->BindingType,
                        binding->Flags);
                }
            }
        }

        RecordDeviceAddressBindings(current->PNext, remainingNodes - 1);
    }

    public string Describe(int maxEntries = 6)
    {
        lock (_summaryLock)
        {
            if (_messages.Count == 0 && _overflowCount == 0)
                return string.Empty;

            StringBuilder builder = new();
            builder.Append("ValidationSummary count=").Append(_messages.Count);
            if (_overflowCount > 0)
                builder.Append(" overflow=").Append(_overflowCount);

            int emitted = 0;
            foreach (KeyValuePair<string, MessageAggregate> pair in _messages)
            {
                if (emitted >= maxEntries)
                    break;

                MessageAggregate aggregate = pair.Value;
                builder.Append(" [").Append(pair.Key)
                    .Append(" hits=").Append(aggregate.Count)
                    .Append(" errors=").Append(aggregate.ErrorCount)
                    .Append(" warnings=").Append(aggregate.WarningCount)
                    .Append(" frames=").Append(aggregate.FirstFrameId)
                    .Append('-').Append(aggregate.LastFrameId).Append(']');
                if (aggregate.ErrorCount > 0 && !string.IsNullOrWhiteSpace(aggregate.LastSample))
                {
                    const int sampleLimit = 768;
                    string sample = aggregate.LastSample.Replace('\r', ' ').Replace('\n', ' ');
                    builder.Append(" sample=")
                        .Append(sample.AsSpan(0, Math.Min(sample.Length, sampleLimit)));
                }
                emitted++;
            }

            return builder.ToString();
        }
    }

    private void Record(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        string message,
        in VulkanSubmissionDiagnosticContext submission)
    {
        string key = BuildKey(callbackData, message, submission.FrameOpKind);
        bool isError = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);
        bool isWarning = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt);
        lock (_summaryLock)
        {
            // Totals remain complete even when bounded sample storage overflows.
            if (isError)
                _errorCount++;
            if (isWarning)
                _warningCount++;
            if (!_messages.TryGetValue(key, out MessageAggregate? aggregate))
            {
                if (_messages.Count >= MaximumMessages)
                {
                    _overflowCount++;
                    return;
                }

                aggregate = new MessageAggregate
                {
                    FirstFrameId = submission.FrameId,
                    FirstSample = message,
                };
                _messages.Add(key, aggregate);
            }

            aggregate.Count++;
            if (isError)
                aggregate.ErrorCount++;
            if (isWarning)
                aggregate.WarningCount++;
            aggregate.LastFrameId = submission.FrameId;
            aggregate.LastSample = message;
        }
    }

    /// <summary>Copies bounded diagnostics on demand; never called by frame submission.</summary>
    internal VulkanValidationDiagnosticSnapshot CaptureSnapshot(
        bool standardValidationEnabled,
        bool synchronizationValidationEnabled,
        bool debugMessengerActive)
    {
        lock (_summaryLock)
        {
            VulkanValidationDiagnosticMessage[] messages = new VulkanValidationDiagnosticMessage[_messages.Count];
            int index = 0;
            foreach ((string identity, MessageAggregate aggregate) in _messages)
            {
                messages[index++] = new()
                {
                    Identity = identity, Count = aggregate.Count,
                    ErrorCount = aggregate.ErrorCount, WarningCount = aggregate.WarningCount,
                    FirstFrameId = aggregate.FirstFrameId, LastFrameId = aggregate.LastFrameId,
                    FirstSample = aggregate.FirstSample, LastSample = aggregate.LastSample,
                };
            }
            return new()
            {
                StandardValidationEnabled = standardValidationEnabled,
                SynchronizationValidationEnabled = synchronizationValidationEnabled,
                DebugMessengerActive = debugMessengerActive,
                ErrorCount = _errorCount, WarningCount = _warningCount,
                SuppressedWarningCount = _suppressedWarningCount,
                OverflowCount = _overflowCount, Messages = messages,
            };
        }
    }

    private static string BuildKey(
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        string message,
        string? frameOpKind)
    {
        string? vuid = ExtractVuid(message);
        string? messageIdName = callbackData->PMessageIdName is null
            ? null
            : Marshal.PtrToStringAnsi((nint)callbackData->PMessageIdName);
        ulong firstObjectHandle = 0;
        ObjectType firstObjectType = ObjectType.Unknown;
        if (callbackData->ObjectCount > 0 && callbackData->PObjects is not null)
        {
            DebugUtilsObjectNameInfoEXT firstObject = callbackData->PObjects[0];
            firstObjectHandle = firstObject.ObjectHandle;
            firstObjectType = firstObject.ObjectType;
        }

        return $"{vuid ?? messageIdName ?? "<unknown>"}:0x{callbackData->MessageIdNumber:X}:frameOp={frameOpKind ?? "<unknown>"}:object={firstObjectType}/0x{firstObjectHandle:X}";
    }

    private static string? ExtractVuid(string message)
    {
        const string prefix = "VUID-";
        int start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        int end = start + prefix.Length;
        while (end < message.Length)
        {
            char c = message[end];
            if (char.IsWhiteSpace(c) || c is ':' or '\'' or '"' or ')' or '(')
                break;
            end++;
        }
        return message[start..end];
    }

    private static string FormatObjects(DebugUtilsMessengerCallbackDataEXT* callbackData)
    {
        if (callbackData->ObjectCount == 0 || callbackData->PObjects is null)
            return string.Empty;

        StringBuilder builder = new();
        ReadOnlySpan<DebugUtilsObjectNameInfoEXT> objects = new(
            callbackData->PObjects,
            checked((int)callbackData->ObjectCount));
        for (int i = 0; i < objects.Length; i++)
        {
            DebugUtilsObjectNameInfoEXT info = objects[i];
            if (builder.Length > 0)
                builder.Append("; ");
            string? objectName = info.PObjectName is null
                ? null
                : Marshal.PtrToStringAnsi((nint)info.PObjectName);
            builder.Append(info.ObjectType).Append(" 0x").Append(info.ObjectHandle.ToString("X"));
            if (!string.IsNullOrWhiteSpace(objectName))
                builder.Append(" '").Append(objectName).Append('\'');
        }
        return builder.ToString();
    }

    private sealed class MessageAggregate
    {
        public int Count;
        public int ErrorCount;
        public int WarningCount;
        public ulong FirstFrameId;
        public ulong LastFrameId;
        public string FirstSample = string.Empty;
        public string LastSample = string.Empty;
    }
}
