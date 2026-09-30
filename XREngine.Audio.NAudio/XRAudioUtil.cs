using NAudio.CoreAudioApi;
using NAudio.Sdl2;
using NAudio.Wave;

namespace XREngine.Audio
{
    public static class XRAudioUtil
    {
        public static IEnumerable<MMDevice> GetInputDeviceEnumerator()
        {
            var enumerator = new MMDeviceEnumerator();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                yield return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)[i];
            enumerator.Dispose();
        }
        public static IEnumerable<MMDevice> GetOutputDeviceEnumerator()
        {
            var enumerator = new MMDeviceEnumerator();
            for (int i = 0; i < WaveOutSdl.DeviceCount; i++)
                yield return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)[i];
            enumerator.Dispose();
        }
        public static MMDevice GetInputDevice(int deviceNumber)
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            enumerator.Dispose();
            return devices.ElementAt(deviceNumber);
        }
        public static MMDevice GetOutputDevice(int deviceNumber)
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            enumerator.Dispose();
            return devices.ElementAt(deviceNumber);
        }
        public static MMDevice[] GetInputDevices()
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            enumerator.Dispose();
            return [.. devices];
        }
        public static MMDevice[] GetOutputDevices()
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            enumerator.Dispose();
            return [.. devices];
        }
    }
}