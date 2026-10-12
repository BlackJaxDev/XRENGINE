# Browser UI font cooking

Ordinary browser publication supports `UITextComponent` with the batched screen-UI
bitmap profile. The default Roboto font keeps its existing cooked atlas and known
notice. An explicitly authored font must have a `.ttf` or `.otf` source below the
project's `Assets` directory, a bitmap R8 atlas, a layout em of 1–512 pixels, and
1–65,536 distinct Unicode-scalar characters. Linked source files or directories
are rejected. The source font is limited to 16 MiB.

Set `BrowserLicenseNoticePath` in that source font's `XRFontImportOptions` to a
regular UTF-8 notice file relative to the project's `Assets` directory, for
example `Fonts/MyFamily/NOTICE.txt`. The notice must be nonempty, unlinked, and
no larger than 256 KiB. Browser publication copies it under `licenses/` with a
stable per-font name. The project owner must verify the font's redistribution
rights and provide the correct notice. Merely setting a notice path does not
establish those rights.

The publisher rasterizes only the selected character repertoire with its
authored layout size using the offline FreeType leaf. The cooked glyphs keep
the authored layout size, bearing, and advance; their atlas positions and UV
extents describe FreeType's rasterized coverage, so the bitmap's antialiasing
can differ from the desktop preview. It writes a bounded,
path-free cooked `FontGlyphSet` with an owned R8 mip atlas. Font identities
include the source contents, project-relative source path, repertoire, layout
size, and authored layout metrics, so different profiles do not alias. The
startup world's dependency closure names each cooked font. The browser verifies
the asset hash, decodes with an explicitly scoped codec, and attaches each font
to its text component before activating the world. Neither source fonts nor
rasterizers run in the browser.

The cooked bitmap font payload is version 3. Each R8 atlas mip is stored raw or
as an LZ4 block, whichever is smaller, with an explicit per-mip encoding tag.
Desktop readers still accept version 2 Brotli payloads. Browsers require version
3; republish older browser bundles to recook their fonts before running them.

The native shared-world package profile deliberately requires a self-contained
world and rejects authored font dependencies until typed native package roots
are supported. Other atlas modes and sources outside project `Assets` remain
unsupported. Font payloads must fit the existing 4 MiB per-asset browser limit;
offline cooking also limits glyph coverage and its temporary RGBA atlas to
64 MiB each.
