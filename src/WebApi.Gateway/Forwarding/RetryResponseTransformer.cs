using Yarp.ReverseProxy.Forwarder;
namespace WebApi.Gateway.Forwarding;
public sealed record ForwardAttemptControl(int Attempt, TimeSpan Timeout, Func<HttpResponseMessage,bool> SuppressResponse,
    Func<HttpResponseMessage,CancellationToken,ValueTask<bool>>? SuppressResponseAsync = null);
public sealed record ForwardAttemptResult(ForwarderError Error, bool Suppressed, int? StatusCode, bool DefinitelyNotSent);

public sealed class RetryResponseTransformer(HttpTransformer inner, ForwardAttemptControl control,
    CancellationToken overall, Action pauseTimeout, Action resumeTimeout) : HttpTransformer
{
    public bool Suppressed { get; private set; }
    public int? StatusCode { get; private set; }
    public override ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string prefix, CancellationToken ct)
        => inner.TransformRequestAsync(context, request, prefix, ct);
    public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken ct)
    {
        if (response is not null)
        {
            StatusCode = (int)response.StatusCode;
            if (!context.Response.HasStarted)
            {
                pauseTimeout();
                try { Suppressed = control.SuppressResponseAsync is not null ? await control.SuppressResponseAsync(response, overall) : control.SuppressResponse(response); }
                finally { resumeTimeout(); }
                if (Suppressed) return false;
            }
        }
        return await inner.TransformResponseAsync(context, response, ct);
    }
    public override ValueTask TransformResponseTrailersAsync(HttpContext context, HttpResponseMessage response, CancellationToken ct)
        => inner.TransformResponseTrailersAsync(context, response, ct);
}
