using NUnit.Framework;
using XREngine.Audio;

namespace XREngine.UnitTests.Audio;

/// <summary>Installs the same explicit desktop audio backends used by the application.</summary>
[SetUpFixture]
public sealed class AudioBackendTestRegistration
{
    [OneTimeSetUp]
    public void Register()
    {
        OpenALBackend.Register();
        NAudioBackend.Register();
        SteamAudioBackend.Register();
    }
}
