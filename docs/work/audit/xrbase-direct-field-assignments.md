# XRBase direct field assignment report

Generated: 2026-02-27 10:25:34

- Rule: classes deriving from `XRBase` should use `SetField(...)` instead of direct backing-field assignment in property setters.
- Scan root: 
- Heuristic: detects likely assignments like `_field = ...` inside `set { ... }` or `set => ...`.
- Excludes: commented-out code, static properties, nested non-XRBase classes.

No likely violations found.

---
Derived classes discovered: 571
Likely violations: 0
