# Unity `.anim` v1 portable fixtures

These fixtures are deterministic YAML inputs for the native importer. They
cover Unity AnimationClip serialized versions 6 and 7 and contain no Unity
project or executable dependency. Hashes are SHA-256 of the exact UTF-8 files
as checked in.

| Fixture | Coverage | SHA-256 |
|---|---|---|
| `compressed-rotation-v6.anim` | compressed quaternion keys | `D0C2EB02CE7EE2B4F5349C2FBD3616D58A58B94CD4A0EE81167691D8733662C7` |
| `packed-dense-constant-v6.anim` | dense and constant packed channels | `B838DEE33CCDF60F1A8863B47362226A7195EA221778208A83DD0C1D7EE436F4` |
| `editable-families-v7.anim` | editable rotation/Euler/position/scale/float/PPtr families | `0718046AC27BC817F67D9292059DCF4BFA55564DF199AD98C1F40C4BA3B4B692` |
| `packed-streamed-v6.anim` | streamed packed channel | `8444F2005BA9E5A28E49D2762E55879C6CA78D472A22847F147D6871D6F792EC` |
| `packed-typed-bindings-v6.anim` | packed integer/discrete and PPtr bindings | `4203E05B65E07208B1035B6B39FE1760B3F6E85F8B2A3A1C081F4335DBB62604` |

The packed typed fixture intentionally uses `m_ConstantClip.data` values as
binding-channel values: integer channels are integral values and PPtr channels
are indices into `pptrCurveMapping`. A packed PPtr value that is not an
integral mapping index is rejected by the importer. Opaque packed CRC/hash
bindings remain adapter-owned and are not represented here.

Verify hashes from the repository root:

```powershell
Get-ChildItem docs/developer-guides/animation/fixtures/unity-anim-v1/*.anim |
  Get-FileHash -Algorithm SHA256
```
