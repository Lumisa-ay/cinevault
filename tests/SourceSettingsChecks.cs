using System.Net;
using System.Text;
using System.Text.Json;
using CineVault.Controllers;
using CineVault.Models;
using CineVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

static class SourceSettingsChecks
{
    public static async Task Run(string root, Action<bool,string> check)
    {
        var data = Path.Combine(root, "source-configuration"); Directory.CreateDirectory(data);
        var keys = new DirectoryInfo(Path.Combine(data, "keys"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Sources:TmdbApiKey"] = "environment-fixture-key" }).Build();
        SourceSettingsStore Open() => new(data, config, DataProtectionProvider.Create(keys));
        var settings = Open();
        check(settings.View().TmdbFromEnvironment && settings.Current.TmdbCredential == "environment-fixture-key", "existing TMDB environment credentials remain available before a web override");
        settings.SaveTmdb(new() { Credential = "saved-fixture-key" });
        check(settings.Current.TmdbCredential == "saved-fixture-key" && !settings.View().TmdbFromEnvironment, "saving a TMDB credential takes effect immediately and overrides the environment");
        var encrypted = File.ReadAllText(Path.Combine(data, "source-settings.protected"));
        check(!encrypted.Contains("saved-fixture-key") && !JsonSerializer.Serialize(settings.View()).Contains("saved-fixture-key"), "credentials never appear in plaintext storage or the configuration view");
        settings = Open();
        check(settings.Current.TmdbCredential == "saved-fixture-key", "protected credentials survive store and protection-provider reconstruction");
        settings.SaveTmdb(new());
        check(settings.Current.TmdbCredential == "saved-fixture-key", "blank TMDB password inputs retain the saved key");
        var rejected = false; try { settings.SaveTmdb(new() { Credential = "bad\r\nX-Header: injected" }); } catch (ArgumentException) { rejected = true; }
        check(rejected && settings.Current.TmdbCredential == "saved-fixture-key", "malformed credentials are rejected without overwriting the saved key");
        for (var i = 0; i < 20; i++) settings.SaveTmdb(new() { Credential = "rotation-fixture-" + i });
        check(Open().Current.TmdbCredential == "rotation-fixture-19", "repeated atomic credential replacements remain readable and retain the latest saved value");
        settings.SaveTmdb(new() { ClearCredential = true });
        check(settings.Current.TmdbCredential == "" && Open().Current.TmdbCredential == "", "clearing a key disables TMDB without silently restoring the environment key");
        settings.SaveTmdb(new() { CredentialType = "bearer", Credential = "Bearer fixture.read.token" });
        var store = new StateStore(Path.Combine(data, "catalogue")); var web = new ConfigurationFixtureWeb();
        var search = new MovieSearchService(web, config, store, settings);
        var tmdb = await search.Search(new(Query: "Fixture", Source: "tmdb", Type: "movie"), CancellationToken.None);
        check(tmdb.Items.Length == 1 && web.Authorization == "Bearer fixture.read.token" && !web.LastUri!.Query.Contains("fixture.read.token"), "saved TMDB bearer credentials are sent only in the authorization header and drive search results");
        check((await search.TestConnection("tmdb", CancellationToken.None)).Success, "TMDB connection testing validates the returned configuration");
        settings.SaveTmdb(new() { Credential = "fixture.v3.key" });
        await search.Search(new(Query: "Fixture", Source: "tmdb", Type: "movie"), CancellationToken.None);
        check(web.LastUri!.Query.Contains("api_key=fixture.v3.key"), "TMDB v3 key replacement also drives the existing query adapter");
        settings.SaveImdb(new() { Mode = "omdb", OmdbApiKey = "omdb-fixture-key" });
        var omdb = await search.Search(new(Query: "Fixture", Source: "imdb"), CancellationToken.None);
        check(omdb.Items.Length == 1 && omdb.Items[0].Imdb == 8.7 && omdb.Items[0].Source!.Contains("第三方") && omdb.Items[0].VoteCount == 1234, "saved OMDb configuration performs search and detail lookup with explicit IMDb rating attribution");
        check((await search.TestConnection("imdb", CancellationToken.None)).Success, "OMDb connection testing uses the selected saved provider");
        settings.SaveImdb(new() { Mode = "omdb" });
        check(settings.Current.OmdbApiKey == "omdb-fixture-key", "blank IMDb provider fields preserve their existing credentials");
        settings.SaveImdb(new() { Mode = "official", ApiKey = "imdb-fixture-key" });
        check(search.Sources("").Single(x => x.Id == "imdb").Status == "needs_key" && !(await search.TestConnection("imdb", CancellationToken.None)).Success, "an IMDb API key alone does not falsely mark the AWS integration ready");
        settings.SaveImdb(new() { Mode = "official", AwsAccessKeyId = "AWSFIXTURE", AwsSecretAccessKey = "aws-fixture-secret", AwsSessionToken = "session-fixture", DataSetId = "data-set-fixture", RevisionId = "revision-fixture", AssetId = "asset-fixture" });
        var official = await search.Search(new(Query: "tt0133093", Source: "imdb"), CancellationToken.None);
        check(official.Items.Length == 1 && official.Items[0].Type == "unknown" && official.Items[0].Year == 0 && official.Items[0].Rating == 8.7, "official IMDb queries expose returned ratings without inventing a year or media category");
        check(web.Authorization!.StartsWith("AWS4-HMAC-SHA256 Credential=AWSFIXTURE/") && web.ImdbKey == "imdb-fixture-key" && web.HasAwsSessionToken && web.LastUri!.Host == "api-fulfill.dataexchange.us-east-1.amazonaws.com", "official IMDb calls use the fixed endpoint, API key, subscription identifiers and signed AWS session credentials");
        check((await search.TestConnection("imdb", CancellationToken.None)).Success, "official IMDb connection testing validates the GraphQL data result");
        var nameInOfficial = await search.Search(new(Query: "Fixture", Source: "imdb"), CancellationToken.None);
        check(nameInOfficial.Items.Length == 0 && nameInOfficial.Sources[0].Status == "needs_query", "official IMDb lookup explains the requirement for an IMDb ID");
        settings.SaveImdb(new() { Mode = "official", DataSetId = "data-set-fixture", RevisionId = "revision-fixture", AssetId = "asset-fixture", ClearSessionToken = true });
        check(settings.Current.AwsSessionToken == "" && settings.Current.AwsSecretAccessKey == "aws-fixture-secret", "temporary AWS session tokens can be removed without discarding long-term credentials");
        var reloaded = Open();
        var publicView = JsonSerializer.Serialize(reloaded.View());
        check(reloaded.Current.ImdbApiKey == "imdb-fixture-key" && !publicView.Contains("imdb-fixture-key") && !publicView.Contains("aws-fixture-secret") && !publicView.Contains("omdb-fixture-key"), "IMDb credentials persist encrypted and never round-trip through view models");
        web.FailWithSecret = true;
        var failedTest = await search.TestConnection("imdb", CancellationToken.None);
        var failedSearch = await search.Search(new(Query: "tt0133093", Source: "imdb"), CancellationToken.None);
        check(!failedTest.Success && !failedTest.Message.Contains("fixture-secret") && !JsonSerializer.Serialize(failedSearch).Contains("fixture-secret"), "upstream exception text containing credentials is redacted from tests and search failures");
        web.FailWithSecret = false;
        settings.SaveImdb(new() { Mode = "dataset", ClearCredentials = true });
        check(settings.Current.ImdbApiKey == "" && settings.Current.OmdbApiKey == "" && settings.Current.AwsSecretAccessKey == "", "clearing IMDb online credentials removes all provider secrets and permits switching back to datasets");
        var auth = typeof(SourceSettingsController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
        check(auth.Roles == "Admin", "all source-settings pages, mutations and tests require the administrator role");
        using var iam = new HttpRequestMessage(HttpMethod.Get, "https://iam.amazonaws.com/?Action=ListUsers&Version=2010-05-08") { Content = new StringContent("") };
        iam.Content.Headers.ContentType = new("application/x-www-form-urlencoded") { CharSet = "utf-8" };
        AwsV4.Sign(iam, "", "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "", "us-east-1", "iam", DateTimeOffset.Parse("2015-08-30T12:36:00Z"));
        check(iam.Headers.GetValues("Authorization").Single().EndsWith("Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7"), "AWS SigV4 matches the published IAM signature vector");
    }
}

sealed class ConfigurationFixtureWeb : IPublicWebClient
{
    public Uri? LastUri; public string? Authorization, ImdbKey; public bool HasAwsSessionToken, FailWithSecret;
    public Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct, long? range = null, string? validator = null)
    { LastUri = uri; return Respond(uri); }
    public Task<HttpResponseMessage> SendApiAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastUri = request.RequestUri; Authorization = request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null;
        ImdbKey = request.Headers.TryGetValues("x-api-key", out var key) ? key.Single() : null;
        HasAwsSessionToken = request.Headers.Contains("x-amz-security-token");
        return Respond(request.RequestUri!);
    }
    private Task<HttpResponseMessage> Respond(Uri uri)
    {
        if (FailWithSecret) return Task.FromException<HttpResponseMessage>(new HttpRequestException("upstream fixture-secret must stay private"));
        var body = uri.AbsolutePath.EndsWith("/configuration") ? "{\"images\":{}}" : uri.Host == "api-fulfill.dataexchange.us-east-1.amazonaws.com" ? """
            {"data":{"title":{"titleText":{"text":"Official Fixture"},"ratingsSummary":{"aggregateRating":8.7,"voteCount":1234}}}}
            """ : uri.Host == "www.omdbapi.com" && uri.Query.Contains("&s=") ? """
            {"Response":"True","Search":[{"Title":"Fixture","imdbID":"tt0133093","Type":"movie","Year":"1999"}]}
            """ : uri.Host == "www.omdbapi.com" ? """
            {"Response":"True","Title":"Fixture","imdbID":"tt0133093","Type":"movie","Year":"1999","imdbRating":"8.7","imdbVotes":"1,234","Genre":"Action, Sci-Fi","Runtime":"136 min","Plot":"Fixture","Poster":"N/A"}
            """ : """
            {"results":[{"id":123,"title":"TMDB Fixture","original_title":"Fixture","release_date":"2023-01-01","genre_ids":[878],"vote_average":8.6,"vote_count":500,"overview":"Fixture only"}]}
            """;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
