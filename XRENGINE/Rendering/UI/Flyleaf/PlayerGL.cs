using System;
using System.ComponentModel;
using System.Threading.Tasks;
using FlyleafLib.MediaPlayer;
using Silk.NET.OpenGL;
using XREngine.Diagnostics;

using FlyleafConfig = FlyleafLib.Config;

namespace XREngine.Rendering.UI;

/// <summary>
/// Temporary shim that adapts Flyleaf's player surface to the engine's OpenGL pipeline.
/// Provides the minimal surface area required by <see cref="UIVideoComponent"/>
/// so the project can compile while the real GPU interop is implemented.
/// </summary>
internal sealed class PlayerGL : IDisposable
{
    private readonly Player _player;
    private readonly VideoGL _video;
    private uint _framebuffer;

    public PlayerGL(FlyleafConfig config, GL _)
    {
        _player = new Player(config);
        _video = new VideoGL(_player.Video);
    }

    public VideoGL Video => _video;

    public uint TargetFramebuffer => _framebuffer;

    public Task<int> OpenAsync(string url)
        => Task.Run(() => SafeOpen(url));

    public void SetTargetFramebuffer(uint framebuffer)
        => _framebuffer = framebuffer;

    public void GLPresent()
    {
        // Rendering path is not wired yet; keep stub for future GPU interop.
    }

    public void Dispose()
    {
        _video.Dispose();
        _player.Dispose();
    }

    private int SafeOpen(string url)
    {
        try
        {
            _player.OpenAsync(url);
            return 0;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Flyleaf GL player failed to open '{url}': {ex.Message}");
            return -1;
        }
    }
}

internal sealed class VideoGL : INotifyPropertyChanged, IDisposable
{
    private readonly Video _inner;

    public VideoGL(Video inner)
    {
        _inner = inner;
        _inner.PropertyChanged += ForwardPropertyChanged;
    }

    public int Width => _inner.Width;
    public int Height => _inner.Height;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ForwardPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => PropertyChanged?.Invoke(this, e);

    public void Dispose()
        => _inner.PropertyChanged -= ForwardPropertyChanged;
}
