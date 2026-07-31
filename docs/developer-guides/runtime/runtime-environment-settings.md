# Runtime Environment Settings

Every environment variable declared by XRENGINE is available in the ImGui
editor under **Global Editor Preferences > Runtime Environment**. The panel
shows its launch value, effective value, category, and how quickly a change can
take effect. Boolean variables have direct On, Off, and Inherit controls; other
values can be edited as text.

Runtime overrides affect only the current process and are not written to the
user or machine environment. Inherit removes the temporary override and
restores the explicit launch value, or the editor preference value when no
launch value was supplied.

Apply modes are:

- **Immediate**: subscribers update without restarting a renderer.
- **Next operation**: the next relevant operation reads the new value.
- **Renderer restart**: restart active renderers from the panel.
- **OpenXR restart**: restart the OpenXR session from the panel.
- **Process restart**: the value is exposed and applied to the process, but the
  owning bootstrap path cannot safely be reconstructed in place.

Validation, tracing, diagnostic, and debugging variables are opt-in and remain
off when unset. Feature paths use their best available implementation by
default and fall back based on capability checks. Environment variables that
disable or downgrade those paths use explicit `XRE_DISABLE_*`, `XRE_BYPASS_*`,
or similarly narrow compatibility names and also remain off when unset.

Explicit launch values remain authoritative until a runtime override is
created. VS Code launch and runnable tasks set environment variables only when
their names and descriptions identify the specific world, role, validation,
profiling, or compatibility scenario being requested.
