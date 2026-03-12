using XREngine.Components;
using XREngine.Data;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapAudioBuilder
{
    public static void AttachMicTo(SceneNode node, out AudioSourceComponent? source, out MicrophoneComponent? mic, out OVRLipSyncComponent? lipSync)
    {
        source = null;
        mic = null;
        lipSync = null;

        if (!RuntimeBootstrapState.Settings.Microphone)
            return;

        source = node.AddComponent<AudioSourceComponent>()!;
        source.Loop = false;
        source.Pitch = 1.0f;
        source.IsDirectional = false;
        source.ConeInnerAngle = 0.0f;
        source.ConeOuterAngle = 90.0f;
        source.ConeOuterGain = 1.0f;

        mic = node.AddComponent<MicrophoneComponent>()!;
        mic.Capture = true;
        mic.Receive = true;
        mic.Loopback = false;

        if (RuntimeBootstrapState.Settings.LipSync)
        {
            lipSync = node.AddComponent<OVRLipSyncComponent>()!;
            lipSync.VisemeNamePrefix = "vrc.v_";
        }
    }

    public static void AddSoundNode(SceneNode rootNode)
    {
        var sound = new SceneNode(rootNode) { Name = "TestSoundNode" };
        if (!sound.TryAddComponent<AudioSourceComponent>(out var soundComp))
            return;

        soundComp!.Name = "TestSound";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var data = Engine.Assets.Load<AudioData>(Path.Combine(desktop, "test.mp3"));
        soundComp.RelativeToListener = true;
        soundComp.Gain = 0.1f;
        soundComp.Loop = true;
        soundComp.StaticBuffer = data;
        soundComp.PlayOnActivate = true;
    }
}