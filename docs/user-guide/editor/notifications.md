# Editor Notifications

The ImGui editor shows a bell and a number at the right end of the menu bar.
Select the bell to show the recorded asset events.

- **Missing or unreadable assets** reports references that could not load.
- **Repaired asset references** reports paths that the engine found at another location or made portable.

Select an event to open and focus **Missing Assets**. This also expands the
panel if it was collapsed. **View > Missing Assets** remains available.
Model import and later asset reports do not force the panel open.

The number counts distinct recorded references in both groups. Repeated reports
for the same reference increase its hit count in the panel, but do not increase
the bell count. Reading an event does not clear it. Clear the corresponding log,
or remove a missing reference in the panel, to reduce the count. The records last
for the current editor process.

You can close docked panels with their ImGui close button. For a panel in a
separate window, you can also use the native window close control or **Alt+F4**.
The editor closes the panel and keeps the main editor window open.
