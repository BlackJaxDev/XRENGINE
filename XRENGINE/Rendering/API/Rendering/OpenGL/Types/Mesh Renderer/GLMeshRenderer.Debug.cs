using Extensions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLMeshRenderer
        {
            // Simple opt-in verbose logger to aid GPU/VAO troubleshooting in debug builds.
            private static volatile bool _verbose = false;
            private static readonly HashSet<string> _enabledDebugCategories = new(StringComparer.OrdinalIgnoreCase)
            {
                "Lifecycle",
                "Buffers",
                "Programs",
                "Render",
                "Atlas",
                "General"
            };

            public static void SetVerbose(bool enabled) => _verbose = enabled;

            public static void EnableCategory(string category)
            {
                if (string.IsNullOrWhiteSpace(category)) return;
                lock (_enabledDebugCategories) _enabledDebugCategories.Add(category);
            }

            public static void DisableCategory(string category)
            {
                if (string.IsNullOrWhiteSpace(category)) return;
                lock (_enabledDebugCategories) _enabledDebugCategories.Remove(category);
            }

            public static void SetCategories(IEnumerable<string> categories)
            {
                lock (_enabledDebugCategories)
                {
                    _enabledDebugCategories.Clear();
                    foreach (var c in categories.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(c))
                            _enabledDebugCategories.Add(c);
                    }
                }
            }

            [Conditional("DEBUG")]
            private static void Dbg(string msg, string category = "General")
            {
                if (!_verbose)
                    return;

                bool enabled;
                lock (_enabledDebugCategories)
                    enabled = _enabledDebugCategories.Contains(category) || _enabledDebugCategories.Contains("All");

                if (enabled)
                    Debug.Out($"[GLMeshRenderer/{category}] {msg}");
            }
        }
    }
}
