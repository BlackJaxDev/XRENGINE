using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenAL;
using XREngine.Audio;

namespace XREngine.UnitTests.Audio;

/// <summary>
/// Checks the OpenAL Soft library that the OpenAL transport loads.
/// The checks use an <c>ALC_SOFT_loopback</c> device. A loopback device renders only on request and opens no output device,
/// so these tests play no sound.
/// </summary>
[TestFixture]
public sealed unsafe class OpenALSoftLoopbackTests
{
    private const int AlcFrequency = 0x1007;
    private const int AlcFormatChannelsSoft = 0x1990;
    private const int AlcFormatTypeSoft = 0x1991;
    private const int AlcStereoSoft = 0x1501;
    private const int AlcShortSoft = 0x1402;

    [Test]
    public void PackagedLibrary_IsOpenALSoft_AndOffersSourceLatency()
    {
        ALContext alc = OpenALSoftLibrary.LoadAlc();
        AL al = OpenALSoftLibrary.LoadAl();

        alc.IsExtensionPresent(null, "ALC_SOFT_loopback").ShouldBeTrue("OpenAL Soft must offer ALC_SOFT_loopback.");
        void* openLoopback = alc.GetProcAddress(null, "alcLoopbackOpenDeviceSOFT");
        ((nint)openLoopback).ShouldNotBe(0);

        Device* device = ((delegate* unmanaged[Cdecl]<byte*, Device*>)openLoopback)(null);
        ((nint)device).ShouldNotBe(0, "OpenAL Soft could not open a loopback device.");
        try
        {
            int* attributes = stackalloc int[]
            {
                AlcFormatChannelsSoft, AlcStereoSoft,
                AlcFormatTypeSoft, AlcShortSoft,
                AlcFrequency, 48000,
                0,
            };
            Context* context = alc.CreateContext(device, attributes);
            ((nint)context).ShouldNotBe(0, "OpenAL Soft could not create a loopback context.");
            try
            {
                alc.MakeContextCurrent(context).ShouldBeTrue();

                string version = al.GetStateProperty(StateString.Version);
                bool sourceLatency = al.IsExtensionPresent("AL_SOFT_source_latency");
                nint getSourcei64v = (nint)al.GetProcAddress("alGetSourcei64vSOFT");
                bool startDelay = al.IsExtensionPresent("AL_SOFT_source_start_delay");
                nint playAtTime = startDelay ? (nint)al.GetProcAddress("alSourcePlayAtTimeSOFT") : 0;

                TestContext.Out.WriteLine($"AL_VERSION: {version}");
                TestContext.Out.WriteLine($"AL_SOFT_source_latency: {sourceLatency}, alGetSourcei64vSOFT: {getSourcei64v != 0}");
                TestContext.Out.WriteLine($"AL_SOFT_source_start_delay: {startDelay}, alSourcePlayAtTimeSOFT: {playAtTime != 0}");

                version.ShouldContain("ALSOFT", Case.Insensitive, "The transport must load OpenAL Soft, not the system router.");
                sourceLatency.ShouldBeTrue("OpenAL Soft must offer AL_SOFT_source_latency for the audio-position clock.");
                getSourcei64v.ShouldNotBe(0);
            }
            finally
            {
                alc.MakeContextCurrent((Context*)null);
                alc.DestroyContext(context);
            }
        }
        finally
        {
            alc.CloseDevice(device);
        }
    }
}
