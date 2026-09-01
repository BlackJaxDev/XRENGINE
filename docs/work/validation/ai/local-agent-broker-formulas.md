# Local agent broker formula preview validation

Validated on Windows on 2026-08-31 with .NET SDK 10.0.400 and WebView2 Runtime
151.0.4129.107. This change replaces the tray's lossy Markdown-to-RichTextBox
response parser with an offline WebView2, markdown-it, and KaTeX preview.

## Runtime results

An isolated disposable WinForms harness instantiated the real `BrokerHistoryForm`
and delivered `BrokerHistoryRecord` snapshots through its existing update path.
It captured the WebView2 output, inspected DOM state, and closed its own window.
No real history record was changed, and no editor process was involved.

- The mass-weighted center example rendered inline variables, `\alpha`,
  `\operatorname`, scripts, and the fraction of two summations correctly.
- A response containing 12 formulas rendered with zero errors in light and dark
  themes. PNGs were visually inspected, including an integral, square root,
  matrix, Maxwell notation, chemistry reaction, and physical units.
- Partial `\(\frac{a}{` remained literal; appending `b}\)` produced one rendered
  formula with no pending source or errors.
- Nine additional formulas covered numeric dollar expressions (`$2 x$`,
  `$2 n + 1$`, `$2 + 2$`, `$2$`), indented display math, blockquotes,
  aligned equations, list nesting, and math in link labels. Zero errors.
- `$5 and $10` and `$5.00` remained currency text. Inline code and both backtick
  and tilde code fences retained their literal TeX and delimiters.
- An unknown TeX command remained visible as escaped source with a diagnostic.
  Script tags did not execute; Markdown images created no image elements;
  unsafe links and KaTeX resource commands did not activate content.
- A native attempt to navigate to an external URL left the preview at its
  local entry page.
- Scrolling upward to 500 pixels remained at 500 after a streamed append and
  theme change; returning to the bottom resumed tail following.
- Selecting `selected` survived both completion of surrounding bold syntax and
  resolution of an earlier reference link, including when selected DOM nodes
  were replaced and when they remained connected.
- The raw view retained the original Markdown/LaTeX response.

## Build and publication

`dotnet build Tools/LocalAgentBroker.Tray/LocalAgentBroker.Tray.csproj` succeeded
with zero warnings and zero errors. `Tools/Setup-LocalAgentBroker.ps1` published
both executables and passed its MCP initialize/list-tools smoke check. All 78
preview assets in that deployment matched source hashes.

Ran `Tools/Reports/Generate-Dependencies.ps1 -NoPromptForUnknownLicenses` (the
actual location of the generator mentioned in AGENTS.md). Added the WebView2
license and consolidated browser-library notices. Excluded unrelated inventory
changes caused by locally absent Flyleaf files and a locally present phonon DLL;
normalized unchanged license texts back to the checkout's CRLF endings.

Scratch logs, captures, and the runtime probe are disposable under
`Build/_AgentValidation/20260831-105952-broker-formulas/`. Required assets and
notices are tracked in `Tools/LocalAgentBroker.Tray/Preview/`; none depend on
scratch output. No unit tests were added or changed, per the feature-validation
policy.

Existing broker transports and tray processes retain their old binaries.
Exit the older companion and restart Codex/the broker task to use the newly
published deployment. Missing WebView2 or failed assets show a raw-text fallback;
this failure path is implemented but a missing-runtime machine was not tested.
