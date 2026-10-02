using System.ComponentModel;

namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        public partial class EngineSettings
        {
            private BrowserWebGpuQualitySettings _browserWebGpuQuality = new();

            [Category("Rendering")]
            [DisplayName("Browser WebGPU Quality")]
            [Description("Explicit sizing, light, shadow, texture, and post-effect policy for the shared browser engine renderer.")]
            public BrowserWebGpuQualitySettings BrowserWebGpuQuality
            {
                get => _browserWebGpuQuality;
                set => SetField(ref _browserWebGpuQuality, value ?? new BrowserWebGpuQualitySettings());
            }
        }
    }
}
