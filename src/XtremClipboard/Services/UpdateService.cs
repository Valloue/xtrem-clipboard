using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Serilog;
using XtremClipboard.Models;
using XtremClipboard.Services.Security;

namespace XtremClipboard.Services;

public interface IUpdateService
{
    /// <summary>
    /// Fermeture propre fournie par App (tray + overlay + instance + flush logs + Exit 0),
    /// exécutée après le lancement de l'installeur.
    /// </summary>
    Action? CleanExitRequested { get; set; }

    Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    Task<string?> DownloadAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyHashAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default);

    Task<UpdateInstallResult> InstallAndRestartAsync(
        string installerPath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Check / téléchargement silencieux / hash / installeur Inno (**D-UPDATE**).
/// Règle auto-update.md : HTTPS + hôte allowlist, sha256 obligatoire, jamais de blocage de l'app.
/// </summary>
public sealed class UpdateService : IUpdateService, IDisposable
{
    public const string ManifestUrl = "https://xtrem.valloue.fr/xtremclipboard/downloads/versions.json";
    public const string DownloadRoot = "https://xtrem.valloue.fr/xtremclipboard/";

    internal const string InstallerArgs =
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ISettingsService _settings;
    private readonly ILogger<UpdateService> _logger;
    private readonly HttpClient _http;
    private readonly string _updatesDir;

    /// <summary>
    /// Fermeture propre fournie par App (tray + overlay + instance + flush logs + Exit 0),
    /// exécutée après le lancement de l'installeur. Fallback : flush + Exit si non branchée.
    /// </summary>
    public Action? CleanExitRequested { get; set; }

    public UpdateService(ISettingsService settings, ILogger<UpdateService> logger)
    {
        _settings = settings;
        _logger = logger;
        _updatesDir = Path.Combine(AppDataPaths.Root, "updates");

        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XtremClipboard/" + ReadLocalVersion());
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var local = ReadLocalVersion();
        _logger.LogInformation("Mise à jour : check versions.json (local={Local})", local);

        VersionInfo? catalog;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Mise à jour : versions.json HTTP {Status}",
                    (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            catalog = await JsonSerializer.DeserializeAsync<VersionInfo>(stream, JsonOptions, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Mise à jour : versions.json timeout — retry au prochain démarrage");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mise à jour : versions.json inaccessible — retry au prochain démarrage");
            return null;
        }

        if (catalog is null || string.IsNullOrWhiteSpace(catalog.Latest))
        {
            _logger.LogWarning("Mise à jour : catalogue vide ou sans latest");
            return null;
        }

        var release = catalog.Versions.FirstOrDefault(v =>
            v.Available &&
            string.Equals(v.Version, catalog.Latest, StringComparison.OrdinalIgnoreCase));
        if (release is null)
        {
            _logger.LogWarning("Mise à jour : latest={Latest} introuvable ou unavailable", catalog.Latest);
            return null;
        }

        if (CompareSemVer(catalog.Latest, local) <= 0)
        {
            _logger.LogInformation("Mise à jour : à jour ({Local} ≥ {Latest})", local, catalog.Latest);
            return null;
        }

        var sha = NormalizeHash(release.Sha256);
        if (sha.Length != 64)
        {
            _logger.LogWarning(
                "Mise à jour : sha256 manquant ou invalide pour {Version} — aucun téléchargement",
                release.Version);
            return null;
        }

        if (string.IsNullOrWhiteSpace(release.File))
        {
            _logger.LogWarning("Mise à jour : champ file vide pour {Version}", release.Version);
            return null;
        }

        if (!TryBuildDownloadUrl(release.File, out var url))
        {
            _logger.LogWarning("Mise à jour : URL invalide ou hôte refusé ({File})", release.File);
            return null;
        }

        Directory.CreateDirectory(_updatesDir);
        var fileName = Path.GetFileName(release.File.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "XtremClipboard-Setup-" + release.Version + ".exe";
        }

        var cached = Path.Combine(_updatesDir, fileName);
        CleanupStaleCache(fileName);

        _logger.LogInformation(
            "Mise à jour : {Local} → {Latest} url={Url}",
            local,
            release.Version,
            url);

        return new UpdateCheckResult
        {
            LocalVersion = local,
            LatestVersion = release.Version,
            DownloadUrl = url,
            Sha256 = sha,
            CachedInstallerPath = cached,
            Notes = release.Notes
        };
    }

    public async Task<string?> DownloadAsync(
        UpdateCheckResult update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (await VerifyHashAsync(update.CachedInstallerPath, update.Sha256, cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Mise à jour : cache valide, pas de retéléchargement ({File})",
                Path.GetFileName(update.CachedInstallerPath));
            return update.CachedInstallerPath;
        }

        DeleteQuiet(update.CachedInstallerPath);
        Directory.CreateDirectory(_updatesDir);
        var partPath = update.CachedInstallerPath + ".part";
        DeleteQuiet(partPath);

        _logger.LogInformation("Mise à jour : téléchargement {Url}", update.DownloadUrl);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
            using var resp = await _http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Mise à jour : download HTTP {Status}",
                    (int)resp.StatusCode);
                return null;
            }

            var total = resp.Content.Headers.ContentLength ?? -1;
            await using (var input = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                             partPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                var lastLogged = 0;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (total > 0)
                    {
                        var pct = copied / (double)total;
                        progress?.Report(pct);
                        var bucket = (int)(pct * 4);
                        if (bucket > lastLogged)
                        {
                            lastLogged = bucket;
                            _logger.LogInformation(
                                "Mise à jour : download {Percent:0}% ({Copied}/{Total})",
                                pct * 100,
                                copied,
                                total);
                        }
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partPath, update.CachedInstallerPath, overwrite: true);
            progress?.Report(1);
            _logger.LogInformation(
                "Mise à jour : download terminé → {File} ({Bytes} o)",
                Path.GetFileName(update.CachedInstallerPath),
                new FileInfo(update.CachedInstallerPath).Length);
            return update.CachedInstallerPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Mise à jour : échec du téléchargement");
            DeleteQuiet(partPath);
            DeleteQuiet(update.CachedInstallerPath);
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Mise à jour : téléchargement annulé");
            DeleteQuiet(partPath);
            throw;
        }
    }

    public async Task<bool> VerifyHashAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var expected = NormalizeHash(expectedSha256);
        if (expected.Length != 64 || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        string actual;
        try
        {
            await using (var stream = new FileStream(
                             filePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                actual = Convert.ToHexString(hash);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Mise à jour : échec vérification SHA-256");
            DeleteQuiet(filePath);
            return false;
        }

        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Mise à jour : SHA-256 OK ({File})",
                Path.GetFileName(filePath));
            return true;
        }

        _logger.LogError(
            "Mise à jour : SHA-256 mismatch (attendu={Expected} obtenu={Actual}) — fichier supprimé",
            expected,
            actual);
        DeleteQuiet(filePath);
        return false;
    }

    public async Task<UpdateInstallResult> InstallAndRestartAsync(
        string installerPath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            _logger.LogError("Mise à jour : installeur introuvable");
            return UpdateInstallResult.MissingInstaller;
        }

        if (string.IsNullOrWhiteSpace(expectedSha256) ||
            !await VerifyHashAsync(installerPath, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogError("Mise à jour : install refusée sans hash valide");
            return UpdateInstallResult.HashInvalid;
        }

        if (!AuthenticodeVerifier.RequireSignedForUpdate(installerPath, _logger))
        {
            _logger.LogError("Mise à jour : install refusée — Authenticode KO");
            // Ne pas supprimer le Setup : SHA-256 est déjà OK ; retry possible.
            return UpdateInstallResult.HashInvalid;
        }

        try
        {
            await _settings.SaveAsync().ConfigureAwait(false);
            _logger.LogInformation("Mise à jour : settings.json sauvé avant install");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mise à jour : sauvegarde settings avant install");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = InstallerArgs,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? _updatesDir
            };

            _logger.LogInformation(
                "Mise à jour : lancement installeur {File} {Args}",
                Path.GetFileName(installerPath),
                InstallerArgs);

            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger.LogWarning("Mise à jour : UAC refusé (Win32 1223)");
            return UpdateInstallResult.UacCancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mise à jour : échec lancement installeur");
            return UpdateInstallResult.Failed;
        }

        _logger.LogInformation("Mise à jour : installeur lancé, fermeture propre de l'app");
        ExitAfterInstallerStarted();
        return UpdateInstallResult.Started;
    }

    public void Dispose() => _http.Dispose();

    internal static string ReadLocalVersion()
    {
        var info = typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            if (plus >= 0)
            {
                info = info[..plus];
            }

            var dash = info.IndexOf('-');
            if (dash >= 0)
            {
                info = info[..dash];
            }

            if (TryParseSemVer(info, out _))
            {
                return info.Trim();
            }
        }

        var v = typeof(UpdateService).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    internal static int CompareSemVer(string left, string right)
    {
        if (!TryParseSemVer(left, out var a) || !TryParseSemVer(right, out var b))
        {
            return 0;
        }

        var c = a.Major.CompareTo(b.Major);
        if (c != 0)
        {
            return c;
        }

        c = a.Minor.CompareTo(b.Minor);
        return c != 0 ? c : a.Build.CompareTo(b.Build);
    }

    private static bool TryParseSemVer(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('.');
        if (parts.Length < 1 ||
            !int.TryParse(parts[0], out var major) ||
            major < 0)
        {
            return false;
        }

        var minor = 0;
        var patch = 0;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], out minor) || minor < 0))
        {
            return false;
        }

        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
        {
            return false;
        }

        version = new Version(major, minor, patch);
        return true;
    }

    private static bool TryBuildDownloadUrl(string file, out string url)
    {
        url = string.Empty;
        if (Uri.TryCreate(file, UriKind.Absolute, out var absolute))
        {
            if (!IsAllowedUpdateUri(absolute))
            {
                return false;
            }

            url = absolute.ToString();
            return true;
        }

        if (!Uri.TryCreate(DownloadRoot, UriKind.Absolute, out var root))
        {
            return false;
        }

        if (!Uri.TryCreate(root, file.TrimStart('/'), out var combined) || !IsAllowedUpdateUri(combined))
        {
            return false;
        }

        url = combined.ToString();
        return true;
    }

    /// <summary>HTTPS uniquement, hôte = xtrem.valloue.fr (pas de http:// ni redirect hors domaine).</summary>
    private static bool IsAllowedUpdateUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.Host, "xtrem.valloue.fr", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Trim();
    }

    private void CleanupStaleCache(string keepFileName)
    {
        try
        {
            if (!Directory.Exists(_updatesDir))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_updatesDir, "XtremClipboard-Setup-*.exe"))
            {
                if (!string.Equals(Path.GetFileName(path), keepFileName, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteQuiet(path);
                }
            }

            foreach (var path in Directory.EnumerateFiles(_updatesDir, "*.part"))
            {
                DeleteQuiet(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mise à jour : nettoyage cache");
        }
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void ExitAfterInstallerStarted()
    {
        if (CleanExitRequested is not null)
        {
            CleanExitRequested.Invoke();
            return;
        }

        // Filet si App n'a pas branché la fermeture propre
        Log.CloseAndFlush();
        Environment.Exit(0);
    }
}
