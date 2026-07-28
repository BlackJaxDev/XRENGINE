# XRENGINE Legal Guide

This folder contains all XRENGINE licensing and contribution documents:

- [Community Source License](../LICENSE.md) — the controlling public license.
- [Commercial and Private-Engine Licensing](COMMERCIAL.md) — Indie and
  Enterprise information and the contact process.
- [Contributing and Contributor Agreement](CONTRIBUTING.md) — contribution
  workflow and required rights grant.

This guide explains the practical boundaries and release steps. It does not
replace the Community Source License or a signed commercial or private-engine
agreement.

## Maintainer Identity

**BlackJax** maintains XRENGINE and represents the company for licensing and
contribution matters. **BlackJaxVR**, **BlackJaxDev**, **Jax**, and
**BlackJax96** are online aliases used by the same person. The controlling
license and signed agreements identify the applicable legal party.

## Quick Rules

| Use | Game code | Engine changes | Required agreement |
| --- | --- | --- | --- |
| Free and transaction-free, public engine changes | May stay closed | Must be public | None |
| Free and transaction-free, private engine changes | May stay closed | Private within signed scope | Private Engine Modification License |
| Monetized, public engine changes | May stay closed | Must be public | Commercial License |
| Monetized, private engine changes | May stay closed | Private within signed scope | Both separate agreements |

“Free” means more than a zero download price. The application must contain no
transactions and produce no Application-related revenue.

## Engine Code Versus Game Code

Engine code generally includes:

- repository projects under `XRENGINE/` and `XREngine.*`;
- the editor, runtime, renderer, audio, input, networking, server, client,
  profiler, importers, and engine tools;
- engine build, packaging, generation, and asset-processing code; and
- XRENGINE-authored shared runtime assets and shaders.

Game code generally remains independent when it:

- lives in a separate game project, assembly, script package, or process;
- contains no copied XRENGINE source;
- uses documented public APIs or extension points; and
- does not patch or replace engine internals.

Third-party dependencies, SDKs, binaries, and assets keep their own licenses.
The `Samples` directory contains reference applications; XRENGINE-authored
sample code remains covered unless a sample states otherwise.

| Example | Classification |
| --- | --- |
| Separate gameplay assembly using public component APIs | Game code |
| Game scripts, levels, art, audio, and narrative | Game content |
| Separate server gameplay module using a public server API | Game code |
| Editing an XRENGINE source file | Engine change |
| Adding a source file to an engine `.csproj` | Engine change |
| Replacing a renderer, physics, editor, asset, or networking subsystem | Engine change |
| Patching non-public engine behavior | Engine change |
| Copying an engine implementation into a game project | Covered engine code |

Static linking, bundling, trimming, single-file publishing, and NativeAOT do
not by themselves make otherwise independent game code an engine change.

For an ambiguous proprietary extension, contact
[blackjax0@gmail.com](mailto:blackjax0@gmail.com) before distribution.

## Releasing Under the Community License Alone

Before distributing or hosting a game without either separate agreement:

- confirm every user can obtain and use it without payment;
- remove purchases, paid access, DLC, virtual currency, marketplaces,
  Application-related donations, advertising, sponsorships, and other
  monetization;
- publish the exact source for every engine change;
- include the Community Source License and preserve third-party notices;
- provide a working source link in credits or legal notices; and
- ensure the game EULA preserves separate rights in XRENGINE code.

For an unmodified engine, link to the exact official release or commit. For a
modified engine, publish complete matching source, project files, generators,
build instructions, and modification history. Keep it available during the
release or deployment and for at least three years afterward.

Do not publish credentials, signing keys, personal data, production secrets,
or third-party source You cannot redistribute.

## Server and Hosted Changes

When anyone outside the operator’s company can use a modified XRENGINE server,
publish the matching engine changes when deployment begins. Provide the link
through the launcher, login, server listing, connection message, legal page,
or accompanying website.

Employees and confidential contractors working only for the company are
internal. Players, invited testers, customers, community members, and friends
are external users.

## Suggested Application Notice

> This application uses XRENGINE. XRENGINE engine components are licensed
> separately under the XRENGINE Community Source License 1.0. The matching
> engine source is available at: [EXACT SOURCE URL]. Commercial and
> private-engine licensing: blackjax0@gmail.com.

## Suggested Proprietary EULA Carve-Out

> XRENGINE engine components included with this product are not licensed under
> this game EULA. They are provided under the XRENGINE Community Source
> License 1.0 or applicable signed XRENGINE agreements. Nothing in this game
> EULA limits rights granted in publicly licensed XRENGINE components.

Proprietary restrictions may still apply to independent game code and content.

## Previous Licenses

The Community Source License is offered as an additional option for covered
XRENGINE versions dating back to the first public release on 12 June 2023.
Earlier versions also remain available under the GPLv3 or AGPLv3 terms that
originally applied to them. The current license does not revoke those earlier
rights or impose new obligations on conduct before 27 July 2026.
