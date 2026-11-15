namespace CineVault.Models;

// Credentials are only used by server services; never use this type as a controller response or view model.
public sealed class SourceCredentials
{
    public bool TmdbOverride { get; set; }
    public string TmdbType { get; set; } = "api_key";
    public string TmdbCredential { get; set; } = "";
    public string ImdbMode { get; set; } = "dataset";
    public string ImdbApiKey { get; set; } = "";
    public string OmdbApiKey { get; set; } = "";
    public string AwsAccessKeyId { get; set; } = "";
    public string AwsSecretAccessKey { get; set; } = "";
    public string AwsSessionToken { get; set; } = "";
    public string DataSetId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string ResourceIndexUrl { get; set; } = "";
    public string ResourceIndexKey { get; set; } = "";
    public string QbitUrl { get; set; } = "";
    public string QbitUserName { get; set; } = "admin";
    public string QbitPassword { get; set; } = "";
    public string QbitSaveRoot { get; set; } = "/downloads";
    public SourceCredentials Snapshot() => (SourceCredentials)MemberwiseClone();
    public bool OfficialReady => new[] { ImdbApiKey, AwsAccessKeyId, AwsSecretAccessKey, DataSetId, RevisionId, AssetId }.All(x => x.Length > 0);
    public static SourceCredentials Environment(IConfiguration config) => new() { TmdbCredential = config["Sources:TmdbApiKey"]?.Trim() ?? "" };
}

public sealed class TmdbSettingsInput
{
    public string CredentialType { get; set; } = "api_key";
    public string? Credential { get; set; }
    public bool ClearCredential { get; set; }
}
public sealed class ImdbSettingsInput
{
    public string Mode { get; set; } = "dataset";
    public string? ApiKey { get; set; }
    public string? OmdbApiKey { get; set; }
    public string? AwsAccessKeyId { get; set; }
    public string? AwsSecretAccessKey { get; set; }
    public string? AwsSessionToken { get; set; }
    public string? DataSetId { get; set; }
    public string? RevisionId { get; set; }
    public string? AssetId { get; set; }
    public bool ClearCredentials { get; set; }
    public bool ClearSessionToken { get; set; }
}
public sealed record SourceConfigurationView(string TmdbType, bool HasTmdbCredential, bool TmdbFromEnvironment,
    string ImdbMode, bool HasImdbApiKey, bool HasOmdbApiKey, bool HasAwsAccessKeyId, bool HasAwsSecretAccessKey,
    bool HasAwsSessionToken, string DataSetId, string RevisionId, string AssetId, bool OfficialReady,
    string ResourceIndexUrl = "", bool HasResourceIndexKey = false, string QbitUrl = "", string QbitUserName = "admin", bool HasQbitPassword = false, string QbitSaveRoot = "/downloads");
public sealed record SourceConnectionResult(bool Success, string Message);
public sealed class BtSettingsInput
{
    public string? ResourceIndexUrl { get; set; }
    public string? ResourceIndexKey { get; set; }
    public string? QbitUrl { get; set; }
    public string? QbitUserName { get; set; }
    public string? QbitPassword { get; set; }
    public string QbitSaveRoot { get; set; } = "/downloads";
    public bool ClearResourceIndexKey { get; set; }
    public bool ClearQbitPassword { get; set; }
}
