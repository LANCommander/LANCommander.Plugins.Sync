using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LANCommander.Plugins.Sync.Protocol;
using LANCommander.Plugins.Sync.Remotes;

namespace LANCommander.Plugins.Sync.Syncing;

/// <summary>Calls the Sync plugin on a remote server.</summary>
/// <remarks>
/// The client has no timeout of its own: a catalog of a large library takes a while to put together
/// and packages can be many gigabytes. Callers that want a quick answer pass a token that expires.
/// </remarks>
public sealed class RemoteClient(IHttpClientFactory clientFactory)
{
    public const string HttpClientName = "LANCommander.Plugins.Sync";

    public Task<RemoteInfo> GetInfoAsync(RemoteServer remote, CancellationToken cancellationToken) =>
        SendAsync<RemoteInfo>(remote, HttpMethod.Get, SyncProtocol.InfoPath, null, cancellationToken);

    public Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(RemoteServer remote, CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<CollectionInfo>>(remote, HttpMethod.Get, SyncProtocol.CollectionsPath, null, cancellationToken);

    public Task<CatalogResponse> GetCatalogAsync(RemoteServer remote, CatalogRequest request, CancellationToken cancellationToken) =>
        SendAsync<CatalogResponse>(remote, HttpMethod.Post, SyncProtocol.CatalogPath, request, cancellationToken);

    /// <summary>Starts downloading a package. Dispose the result to end the download.</summary>
    public async Task<RemotePackage> OpenPackageAsync(RemoteServer remote, ExportRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(remote, HttpMethod.Post, SyncProtocol.ExportPath, request, cancellationToken);

        try
        {
            return new RemotePackage(response, await response.Content.ReadAsStreamAsync(cancellationToken));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    async Task<T> SendAsync<T>(RemoteServer remote, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(remote, method, path, body, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(SyncProtocol.JsonOptions, cancellationToken)
               ?? throw new RemoteServerException($"{remote.Name} sent an empty answer.");
    }

    async Task<HttpResponseMessage> SendAsync(RemoteServer remote, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var client = clientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(method, new Uri($"{RemoteServers.NormalizeAddress(remote.Address)}/api/Plugins/{SyncProtocol.RoutePrefix}/{path}"));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remote.AccessToken);

        if (body != null)
            request.Content = JsonContent.Create(body, body.GetType(), options: SyncProtocol.JsonOptions);

        HttpResponseMessage response;

        try
        {
            // Headers only, so a package streams rather than being read into memory
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteServerException($"{remote.Name} could not be reached: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var detail = await ReadDetailAsync(response, cancellationToken);

            throw new RemoteServerException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => $"{remote.Name} didn't accept the access token. It may have expired or been revoked; make a new one there.",
                HttpStatusCode.Forbidden => $"{remote.Name} turned the access token away. It must belong to an administrator and be allowed the Sync plugin.",
                HttpStatusCode.NotFound when path == SyncProtocol.InfoPath => $"{remote.Name} doesn't have the Sync plugin installed, or isn't a LANCommander server.",
                _ => $"{remote.Name} answered {(int)response.StatusCode} {response.ReasonPhrase}{(detail == null ? "" : $": {detail}")}",
            }, statusCode: response.StatusCode);
        }
    }

    static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim().Trim('"');

            return text.Length is > 0 and <= 500 ? text : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>A package being downloaded from a remote server.</summary>
public sealed class RemotePackage(HttpResponseMessage response, Stream content) : IDisposable
{
    public Stream Content { get; } = content;

    /// <summary>The package's size, when the remote server says.</summary>
    public long? Length => response.Content.Headers.ContentLength;

    public void Dispose()
    {
        Content.Dispose();
        response.Dispose();
    }
}

/// <summary>A remote server couldn't be reached or refused a request, with a message for the administrator.</summary>
public sealed class RemoteServerException(string message, Exception? innerException = null, HttpStatusCode? statusCode = null)
    : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
