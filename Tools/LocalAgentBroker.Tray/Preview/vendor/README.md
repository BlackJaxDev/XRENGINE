# Offline preview dependencies

These pinned browser distributions are copied unchanged from their npm tarballs.
No Node installation, CDN, package-manager step, or network access is needed to
build or run the preview. `LocalAgentBroker.Tray.csproj` copies this directory
into build and publish output.

| Component | Source | License |
|---|---|---|
| KaTeX 0.18.4 | https://registry.npmjs.org/katex/-/katex-0.18.4.tgz | MIT |
| KaTeX fonts | Included in the same KaTeX distribution; font notice from https://github.com/KaTeX/katex-fonts/blob/master/LICENSE | MIT |
| mhchem and copy-tex | KaTeX 0.18.4 `dist/contrib/` | MIT; mhchem also retains its upstream Apache-2.0 notice |
| markdown-it 15.0.0 | https://registry.npmjs.org/markdown-it/-/markdown-it-15.0.0.tgz | MIT; bundled dependencies MIT/BSD-2-Clause |
| Microsoft.Web.WebView2 1.0.4129.50 | NuGet, referenced by the tray project | BSD-style Microsoft license in `WEBVIEW2-LICENSE.txt` |

The KaTeX tarball SHA-512 is
`IMPntbRLOU+eu88XDiFKqQ8Akhr9Tv7jDMXqPhjG9SI1JMA4DIgXk4x9k4skJz2NZJXBRbC+2pYBLj9olqcZow==`.
The markdown-it tarball SHA-512 is
`Lf8ajvVNdRpzSNB4VegxNy7gjs8gU35l4b4+ET49LrQC5PKYwLZ72u60LeJ9gv3qiaesuYjJWCyVeQmv/QWKQw==`.
Both were verified against the npm registry integrity fields before extraction.

`katex/THIRD-PARTY-NOTICES.txt` and
`markdown-it/THIRD-PARTY-NOTICES.txt` consolidate the retained licenses for the
dependency inventory. The latter also includes notices for the dependency set
declared by markdown-it (including argparse, used by its CLI). Every shipped
font format referenced by the upstream stylesheet is retained.

To update, obtain owner approval for the dependency upgrade, verify the new
tarball integrity, replace only the upstream assets, update notices and this
manifest, and run `pwsh Tools/Reports/Generate-Dependencies.ps1`. Do not edit
minified upstream files. Validate the actual Windows viewer, including math,
code, currency, streaming, themes, copying, and blocked remote content.
