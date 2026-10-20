namespace XREngine.Rendering;

/// <summary>Records one directory used to find shader source files.</summary>
internal readonly record struct ShaderSourceDirectoryDependency(string Path, long LastWriteTimeUtcTicks);
