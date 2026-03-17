using XREngine.Components;
using XREngine.Data;
using XREngine.Scene;

namespace XREngine.Runtime.AudioIntegration;

public static class BootstrapAudioWorldBuilder
{
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