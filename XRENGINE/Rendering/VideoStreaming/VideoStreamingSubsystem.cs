using Silk.NET.OpenGL;
using System;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

public sealed class VideoStreamingSubsystem(IHlsStreamResolver resolver, Func<GL, IMediaStreamSession> sessionFactory)
{
    public IHlsStreamResolver Resolver => resolver;

    public static VideoStreamingSubsystem CreateDefault(Func<GL, IMediaStreamSession> sessionFactory)
        => new(new TwitchHlsStreamResolver(), sessionFactory);

    public Task<ResolvedStream> ResolveAsync(string source, CancellationToken cancellationToken)
        => resolver.ResolveAsync(source, cancellationToken);

    public IMediaStreamSession CreateSession(GL gl)
        => sessionFactory(gl);
}
