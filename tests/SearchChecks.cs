using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CineVault.Models;
using CineVault.Services;
using Microsoft.Extensions.Configuration;

static class SearchChecks
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        var store = new StateStore(Path.Combine(directory, "search"));
        var web = new SearchFixtureWeb();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Sources:TmdbApiKey"] = "fixture-private-key", ["Sources:ImdbDirectory"] = Path.Combine(directory, "imdb")
        }).Build();
        var service = new MovieSearchService(web, config, store);
        var sources = service.Sources("电影 & title");
        check(sources.Length == 5 && sources.Single(x => x.Id == "tmdb").Status == "ready" && sources.Single(x => x.Id == "imdb").Status == "needs_data", "five original sources report real configuration and integration states");
        check(sources.Where(x => x.Id is "douban" or "justwatch").All(x => x.Status == "external") && sources.All(x => x.Url.Contains("%26")), "web-only sources are explicit and search links encode the query");
        var missingKey = new MovieSearchService(web, new ConfigurationBuilder().Build(), store);
        check(missingKey.Sources("").Single(x => x.Id == "tmdb").Status == "needs_key", "missing TMDB credentials do not produce fake search results");
        foreach (var request in new[] { new MovieSearchRequest(Source: "unknown"), new MovieSearchRequest(MinRating: double.NaN), new MovieSearchRequest(Page: 0), new MovieSearchRequest(Query: new string('a', 121)) })
        {
            var rejected = false; try { MovieSearchService.Validate(request); } catch (ArgumentException) { rejected = true; }
            check(rejected, "invalid source search request is rejected");
        }
        var archive = await service.Search(new(Source: "archive", MinRating: 8), CancellationToken.None);
        check(archive.Items.Length == 1 && archive.Items[0].Rating == 8 && archive.Items[0].Imdb is null && archive.Items[0].RatingSource!.Contains("5 分制"), "Archive ratings normalize from five and never masquerade as IMDb scores");
        check(archive.Items[0].Resources.Length == 0 && archive.Items[0].VoteCount == 12 && !archive.Items[0].Description.Contains('<'), "search results preserve vote counts, strip description HTML and defer file lookup");
        var withUnrated = await service.Search(new(Source: "archive", MinRating: 8, IncludeUnrated: true), CancellationToken.None);
        check(withUnrated.Items.Length == 2 && withUnrated.Items.Any(x => x.Type == "tv" && x.Rating == 0), "unrated inclusion and television classification are explicit");
        var movieOnly = await service.Search(new(Source: "archive", Type: "movie"), CancellationToken.None);
        check(movieOnly.Items.All(x => x.Type == "movie"), "movie filter is enforced even when the upstream returns mixed types");
        await service.Search(new(Query: "title\" OR *:*", Source: "archive"), CancellationToken.None);
        check(Uri.UnescapeDataString(web.Requested.Last().Query).Contains("title:\"title\\\" OR *:*\""), "Archive queries quote user text without admitting search operators");
        var resolved = await service.ArchiveResources("fixture_film", CancellationToken.None);
        check(resolved.Resources.Length == 1 && resolved.Resources[0].Url.EndsWith("film%20one.mp4") && resolved.Description.Contains("creativecommons.org"), "metadata resolves public video files, rejects private and traversal entries, and preserves license attribution");
        await service.Search(new(Source: "archive"), CancellationToken.None);
        check(store.Catalogue.Items.Single(x => x.Id == resolved.Id).Resources.Length == 1, "repeating a search preserves previously resolved download resources");
        var persisted = new StateStore(Path.Combine(directory, "search"));
        check(persisted.Catalogue.Items.Single(x => x.Id == resolved.Id).Source == "Internet Archive", "source provenance and search results survive store reconstruction");
        web.Restricted = true;
        var rejectedRestricted = false; try { await service.ArchiveResources("fixture_film", CancellationToken.None); } catch (InvalidDataException) { rejectedRestricted = true; }
        check(rejectedRestricted && store.Catalogue.Items.Single(x => x.Id == resolved.Id).Resources.Length == 1, "restricted metadata cannot replace existing resources with an inaccessible download");
        web.Restricted = false;
        var tmdb = await service.Search(new(Source: "tmdb", Type: "movie"), CancellationToken.None);
        check(tmdb.Items.Length == 1 && tmdb.Items[0].Rating == 8.6 && tmdb.Items[0].Genres.Contains("科幻") && tmdb.Items[0].Resources.Length == 0 && tmdb.Items[0].Imdb is null, "TMDB maps its own score and genres without inventing IMDb or download links");
        web.FailTmdb = true;
        var partial = await service.Search(new(Query: "fixture", Source: "all"), CancellationToken.None);
        check(partial.Items.Length == 2 && partial.Sources.Single(x => x.Id == "tmdb").Status == "error" && !JsonSerializer.Serialize(partial).Contains("fixture-private-key"), "one failing source does not hide successful results or leak API keys");
        var beforeFailure = store.Catalogue; web.FailArchive = true;
        var failure = await service.Search(new(Source: "archive"), CancellationToken.None);
        check(failure.Items.Length == 0 && failure.Sources[0].Status == "error" && ReferenceEquals(beforeFailure, store.Catalogue), "complete search failure preserves the resource library");
        var imdbDir = Path.Combine(directory, "imdb"); Directory.CreateDirectory(imdbDir);
        void WriteGzip(string name, string text)
        {
            using var stream = new GZipStream(File.Create(Path.Combine(imdbDir, name + ".gz")), CompressionMode.Compress);
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
        WriteGzip("title.basics.tsv", "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres\n" +
            "tt0000001\tmovie\tFixture Film\tFixture Film\t0\t2020\t\\N\t95\tDrama,Sci-Fi\n" +
            "tt0000002\ttvSeries\tFixture Series\tFixture Series\t0\t2021\t\\N\t45\tDrama\n");
        WriteGzip("title.ratings.tsv", "tconst\taverageRating\tnumVotes\n" + "tt0000001\t8.7\t1234\n" + "tt0000002\t6.0\t100\n");
        var requestsBeforeImdb = web.Requested.Count;
        var imdb = await service.Search(new(Query: "Fixture", Source: "imdb", MinRating: 8), CancellationToken.None);
        check(imdb.Items.Length == 1 && imdb.Items[0].Rating == 8.7 && imdb.Items[0].Imdb == 8.7 && imdb.Items[0].VoteCount == 1234 && web.Requested.Count == requestsBeforeImdb, "IMDb queries local compressed datasets and joins ratings without network requests");
    }
}

sealed class SearchFixtureWeb : IPublicWebClient
{
    public List<Uri> Requested { get; } = [];
    public bool FailTmdb, FailArchive, Restricted;
    public Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct, long? range = null, string? validator = null)
    {
        Requested.Add(uri);
        if (uri.Host == "api.themoviedb.org" && FailTmdb)
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("upstream failed: " + uri));
        if (uri.Host == "archive.org" && FailArchive)
            return Task.FromException<HttpResponseMessage>(new PublicAddressRejectedException("archive.org"));
        var body = uri.AbsolutePath.StartsWith("/metadata/") ? """
            {"metadata":{"identifier":"fixture_film","description":"<b>Fixture film</b>","licenseurl":"https://creativecommons.org/publicdomain/mark/1.0/"},"files":[{"name":"film one.mp4","format":"MPEG4","size":"1024"},{"name":"../escape.mp4"},{"name":"private.mp4","private":"true"},{"name":".hidden/../escape.mp4"},{"name":"index.html"}]}
            """ : uri.Host == "api.themoviedb.org" ? """
            {"results":[{"id":123,"title":"TMDB Fixture","original_title":"Fixture","release_date":"2023-01-01","genre_ids":[878,18],"vote_average":8.6,"vote_count":500,"poster_path":"/fixture.jpg","overview":"Fixture only"}]}
            """ : """
            {"response":{"docs":[{"identifier":"fixture_film","title":"Fixture Film","year":"2020","collection":["feature_films"],"avg_rating":4,"num_reviews":12,"description":"<p>Fixture only</p>","subject":["Drama"]},{"identifier":"fixture_series","title":"Fixture Series","year":"2021","collection":["television"],"subject":"Drama"}]}}
            """;
        if (Restricted) body = body.Replace("\"identifier\":\"fixture_film\"", "\"identifier\":\"fixture_film\",\"access-restricted-item\":\"true\"");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
