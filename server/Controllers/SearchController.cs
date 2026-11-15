using CineVault.Models;
using CineVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CineVault.Controllers;

[ApiController, Authorize, Route("api")]
public sealed class SearchController(MovieSearchService search) : ControllerBase
{
    [HttpGet("sources")]
    public MovieSource[] Sources() => search.Sources("");

    [HttpPost("search")]
    public async Task<MovieSearchResponse> Search(MovieSearchRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        return await search.Search(request, timeout.Token);
    }

    [HttpPost("resources/archive/{identifier}")]
    public Task<MediaItem> ArchiveResources(string identifier, CancellationToken ct) => search.ArchiveResources(identifier, ct);
}
