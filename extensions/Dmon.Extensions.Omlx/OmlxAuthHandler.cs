namespace Dmon.Extensions.Omlx;

public sealed class OmlxAuthHandler : DelegatingHandler
{
    private readonly string _apiKey;

    public OmlxAuthHandler(string apiKey)
    {
        _apiKey = apiKey;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
