using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("XREngine.UnitTests")]
[assembly: MetadataUpdateHandler(typeof(XREngine.Rendering.RendererManagedHotReload))]
