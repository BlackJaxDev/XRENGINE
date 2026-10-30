using XREngine.Audio;
using XREngine.Components;
using XREngine.Data;
using XREngine.Scene;

namespace XREngine.Editor.Publishing;

/// <summary>Admits decoded PCM assets for browser cooking without changing authored samples.</summary>
internal static class BrowserAudioCookProfile
{
    /// <summary>Checks the PCM that active listener-relative sources attach to one output context.</summary>
    internal static void ValidateStaticResidency(XRWorld world)
    {
        long residentBytes = 0;
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        foreach (XRScene scene in world.Scenes)
        {
            if (!scene.IsVisible || scene.IsEditorOnly)
                continue;
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, true);
        }

        void Visit(SceneNode node, bool parentActive)
        {
            if (!visited.Add(node))
                return;
            bool active = parentActive && node.IsActiveSelf && !node.IsEditorOnly;
            if (!active)
                return;
            foreach (XRComponent component in node.Components)
            {
                if (component is not AudioSourceComponent
                    { IsActive: true, RelativeToListener: true, StaticBuffer: { Data: { } data } })
                    continue;
                residentBytes = checked(residentBytes + data.Length);
                if (residentBytes > WebAudioPcmProfile.MaximumBufferBytes)
                    throw new NotSupportedException($"BrowserCook.AudioStaticResidencyExceeded: world '{world.Name}', source '{node.GetPath()}' needs {residentBytes} PCM bytes in one Web Audio output; the limit is {WebAudioPcmProfile.MaximumBufferBytes} bytes.");
            }
            foreach (var transform in node.Transform.Children)
                if (transform.SceneNode is SceneNode child)
                    Visit(child, active);
        }
    }

    internal static unsafe void Validate(AudioData audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        try
        {
            DataSource data = audio.Data
                ?? throw new InvalidDataException("Audio data has no PCM payload.");
            SampleFormat format = audio.Type switch
            {
                AudioData.EPCMType.Byte => SampleFormat.Byte,
                AudioData.EPCMType.Short => SampleFormat.Short,
                AudioData.EPCMType.Float => SampleFormat.Float,
                _ => throw new NotSupportedException("Unknown PCM sample type."),
            };
            int length = checked((int)data.Length);
            WebAudioPcmProfile.ValidateLayout(length, audio.Frequency, audio.ChannelCount, format);
            if (data.Address.Pointer is null)
                throw new InvalidDataException("Audio PCM storage is unavailable.");
            WebAudioPcmProfile.Validate(new ReadOnlySpan<byte>(data.Address.Pointer, length),
                audio.Frequency, audio.ChannelCount, format);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        {
            throw new NotSupportedException($"BrowserCook.AudioPcmUnsupported: audio '{audio.Name}' ({audio.ID}): {error.Message}", error);
        }
    }
}
