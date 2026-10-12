# Cooked mesh reload asset read

`XRMesh.Reload(string)` now captures one `RuntimeAssetReadLease`. It checks host-file access before it reads the cooked mesh bytes. The lease keeps the selected source and rejects a retired source. Deserialization and meshlet payload validation run after the read lease is released. The payload format, geometry-owner check, and temporary loaded-mesh cleanup do not change.

The installed desktop asset source reads the bytes in the DirectStorage leaf. A standalone host with no installed source still uses the existing `File.ReadAllBytes` fallback in `RuntimeAssetReadLease`. A browser or runtime-catalog host fails the existing host-file check before a synchronous read. No new dependency or worker is required.

This removes one direct file read from Rendering for `UR03.02b`. It does not close the full runtime file-I/O inventory or `UR02.03b`. The physical fallback in Data remains. Independent source review and the combined Rendering and Host Release build pass with zero warnings and errors. No browser execution or desktop reload result is claimed by this source change.
