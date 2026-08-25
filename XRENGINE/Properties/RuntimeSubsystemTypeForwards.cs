using System.Runtime.CompilerServices;
using XREngine;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Data.Components.Scene;
using XREngine.Rendering.Models;

// These adapters own the implementation now, but previously cooked assets and
// project files identified the public types as members of the XRENGINE facade.
// Keep the forwards deliberately small: they cover the public entry points that
// appear in persisted scene/component and importer data without making the
// facade the owner of either implementation.
[assembly: TypeForwardedTo(typeof(HumanoidComponent))]
[assembly: TypeForwardedTo(typeof(TransformParameterDriverComponent))]
[assembly: TypeForwardedTo(typeof(AudioListenerComponent))]
[assembly: TypeForwardedTo(typeof(AudioSourceComponent))]
[assembly: TypeForwardedTo(typeof(VRHeadsetComponent))]
[assembly: TypeForwardedTo(typeof(ModelImporter))]
[assembly: TypeForwardedTo(typeof(ModelImportOptions))]
