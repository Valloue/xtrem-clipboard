using System.Text.Json.Serialization;

namespace XtremClipboard.Models;

/// <summary>Catalogue distant <c>versions.json</c> (auto-update, D-UPDATE).</summary>
public sealed class VersionInfo
{
    public string Product { get; set; } = string.Empty;

    public string Latest { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public List<VersionRelease> Versions { get; set; } = [];
}

/// <summary>Une entrée du tableau <c>versions</c>.</summary>
public sealed class VersionRelease
{
    public string Version { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Date { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    public bool Available { get; set; }

    public bool Recommended { get; set; }

    public string? Notes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

/// <summary>Résultat d'un check (update dispo + chemins).</summary>
public sealed class UpdateCheckResult
{
    public required string LocalVersion { get; init; }

    public required string LatestVersion { get; init; }

    public required string DownloadUrl { get; init; }

    public required string Sha256 { get; init; }

    public required string CachedInstallerPath { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Résultat du lancement d'installation.</summary>
public enum UpdateInstallResult
{
    Started,
    MissingInstaller,
    HashInvalid,
    UacCancelled,
    Failed
}
