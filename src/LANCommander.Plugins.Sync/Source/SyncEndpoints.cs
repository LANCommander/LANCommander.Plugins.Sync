using LANCommander.Plugins.Sync.Protocol;
using LANCommander.Plugins.Sync.Remotes;
using LANCommander.Server.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace LANCommander.Plugins.Sync.Source;

/// <summary>
/// What servers syncing from this one call, under <c>/api/Plugins/Sync</c>. Administrators only: the
/// syncing server signs in with a personal access token an administrator made here.
/// </summary>
/// <remarks>
/// Bodies are read and written with <see cref="SyncProtocol.JsonOptions"/> rather than the host's JSON
/// settings, so both ends agree however each server is configured.
/// </remarks>
public sealed class SyncEndpoints : IServerEndpointExtension
{
    public string RoutePrefix => SyncProtocol.RoutePrefix;

    public PluginAccessPolicy Access => PluginAccessPolicy.Administrator;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet($"/{SyncProtocol.InfoPath}", (RemoteServers remotes) =>
            Results.Json(new RemoteInfo(remotes.InstanceId, SyncProtocol.Version, SyncPlugin.PluginVersion), SyncProtocol.JsonOptions));

        endpoints.MapGet($"/{SyncProtocol.CollectionsPath}", async (CatalogService catalog, CancellationToken cancellationToken) =>
            Results.Json(await catalog.GetCollectionsAsync(cancellationToken), SyncProtocol.JsonOptions));

        endpoints.MapPost($"/{SyncProtocol.CatalogPath}", async (HttpContext http, CatalogService catalog, CancellationToken cancellationToken) =>
        {
            var request = await ReadAsync<CatalogRequest>(http, cancellationToken);

            if (request == null)
                return Results.BadRequest("A catalog request is needed.");

            return Results.Json(await catalog.GetCatalogAsync(request, cancellationToken), SyncProtocol.JsonOptions);
        });

        endpoints.MapPost($"/{SyncProtocol.ExportPath}", async (HttpContext http, CatalogService catalog, CancellationToken cancellationToken) =>
        {
            var request = await ReadAsync<ExportRequest>(http, cancellationToken);

            if (request == null)
                return Results.BadRequest("An export request is needed.");

            Func<Stream, CancellationToken, Task> write;

            try
            {
                write = await catalog.PrepareExportAsync(request, cancellationToken);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            // The package is zipped straight into the response, which writes synchronously as each entry closes
            if (http.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
                bodyControl.AllowSynchronousIO = true;

            http.Response.ContentType = SyncProtocol.PackageContentType;

            await write(http.Response.Body, cancellationToken);

            return Results.Empty;
        });
    }

    static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(SyncProtocol.JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
