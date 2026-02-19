using XREngine.Components;
using XREngine.Components.Scene;
using XREngine.Data;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    //Tests for audio sources and listeners.

    public static class Audio
    {
        public static void AttachMicTo(SceneNode node, out AudioSourceComponent? source, out MicrophoneComponent? mic, out OVRLipSyncComponent? lipSync)
        {
            source = null;
            mic = null;
            lipSync = null;

            if (!Toggles.Microphone)
                return;

            source = node.AddComponent<AudioSourceComponent>()!;
            source.Loop = false;
            source.Pitch = 1.0f;
            source.IsDirectional = false;
            source.ConeInnerAngle = 0.0f;
            source.ConeOuterAngle = 90.0f;
            source.ConeOuterGain = 1.0f;

            mic = node.AddComponent<MicrophoneComponent>()!;
            mic.Capture = true;//!isServer;
            mic.Receive = true;//isServer;
            mic.Loopback = false; //For testing, set to true to hear your own voice, unless both capture and receive are true on the client.

            if (Toggles.LipSync)
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
            //var data = Engine.Assets.LoadEngineAsset<AudioData>("Audio", "test16bit.wav");
            //data!.ConvertToMono(); //Convert to mono for 3D audio - stereo will just play equally in both ears
            soundComp.RelativeToListener = true;
            //soundComp.ReferenceDistance = 1.0f;
            ////soundComp.MaxDistance = 100.0f;
            //soundComp.RolloffFactor = 1.0f;
            soundComp.Gain = 0.1f;
            soundComp.Loop = true;
            soundComp.StaticBuffer = data;
            soundComp.PlayOnActivate = true;
        }
    }
}
