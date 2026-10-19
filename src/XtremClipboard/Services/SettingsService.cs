using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XtremClipboard.Models;

namespace XtremClipboard.Services;

/// <summary>
/// Persistance settings.json sous LocalAppData (D-SETTINGS, D-CLIP-LOCAL).
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _debounceCts;

    public AppSettings Current { get; private set; } = new();

    public string DataDirectory { get; }

    public event EventHandler? SettingsChanged;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        AppDataPaths.EnsureMigrated();
        DataDirectory = AppDataPaths.Root;
        Directory.CreateDirectory(DataDirectory);
        _settingsPath = Path.Combine(DataDirectory, "settings.json");

        if (AppDataPaths.MigratedFromLegacy)
            _logger.LogInformation("Données déplacées vers {DataDirectory}", MaskUserPath(DataDirectory));

        // Chargement synchrone au démarrage pour que l'UI ait l'état tout de suite
        LoadCore();
    }

    public Task LoadAsync()
    {
        LoadCore();
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteFileUnlockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Enregistre après un court délai (sliders / changements UI).</summary>
    public void ScheduleSave(int delayMs = 400)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = DebouncedSaveAsync(cts, delayMs);
    }

    private async Task DebouncedSaveAsync(CancellationTokenSource cts, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
            await SaveAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Nouveau ScheduleSave
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec ScheduleSave settings.json");
        }
    }

    private async Task WriteFileUnlockedAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tmp = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Copy(tmp, _settingsPath, overwrite: true);
            File.Delete(tmp);
            _logger.LogInformation(
                "settings.json enregistré (setupCompleted={Completed}, hotkey={Hotkey})",
                Current.IsSetupCompleted,
                Current.Hotkey ?? "(null)");
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec enregistrement settings.json");
            throw;
        }
    }

    /// <summary>Masque le profil utilisateur dans les chemins journalisés (listelog §9).</summary>
    private static string MaskUserPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local, StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + path[local.Length..]
            : path;
    }

    private void LoadCore()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Current = new AppSettings();
                _logger.LogInformation("Aucun settings.json — défauts appliqués");
                return;
            }

            var json = File.ReadAllText(_settingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Current.Exclusions ??= new List<AppExclusionRule>();
            _logger.LogInformation(
                "settings.json chargé (setupCompleted={Completed}, hotkey={Hotkey})",
                Current.IsSetupCompleted,
                Current.Hotkey ?? "(null)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec lecture settings.json — défauts appliqués");
            Current = new AppSettings();
        }
    }
}
