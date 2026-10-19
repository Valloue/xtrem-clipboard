using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XtremClipboard.Models;

namespace XtremClipboard.Services;

/// <summary>
/// Persistance history/index.json + payloads (**D-STORE-LAYOUT**, **D-STORE-LIMITS**).
/// </summary>
public sealed class ClipboardHistoryStore : IClipboardHistoryStore
{
    private const int PreviewMaxChars = 120;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ISettingsService _settings;
    private readonly ILogger<ClipboardHistoryStore> _logger;
    private readonly object _gate = new();
    private readonly string _historyDir;
    private readonly string _payloadsDir;
    private readonly string _indexPath;
    private HistoryIndex _index = new();

    public IReadOnlyList<ClipboardEntry> Entries
    {
        get
        {
            lock (_gate)
                return _index.Entries.ToList();
        }
    }

    public event EventHandler? HistoryChanged;

    public ClipboardHistoryStore(ISettingsService settings, ILogger<ClipboardHistoryStore> logger)
    {
        _settings = settings;
        _logger = logger;
        _historyDir = Path.Combine(settings.DataDirectory, "history");
        _payloadsDir = Path.Combine(_historyDir, "payloads");
        _indexPath = Path.Combine(_historyDir, "index.json");
    }

    public void Load()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_payloadsDir);
            try
            {
                if (!File.Exists(_indexPath))
                {
                    _index = new HistoryIndex();
                    _logger.LogInformation("Aucun index historique — dossier créé");
                    return;
                }

                var json = File.ReadAllText(_indexPath);
                _index = JsonSerializer.Deserialize<HistoryIndex>(json, JsonOptions) ?? new HistoryIndex();
                _logger.LogInformation("Historique chargé : {Count} entrée(s)", _index.Entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec lecture index.json — index réinitialisé");
                TryBackupCorruptIndex();
                _index = new HistoryIndex();
            }
        }
    }

    public ClipboardEntry? AddText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = HashBytes(bytes);
        ClipboardEntry? created = null;
        var raise = false;

        lock (_gate)
        {
            if (IsConsecutiveDuplicate(hash))
            {
                _logger.LogDebug("Texte ignoré (dédup consécutive)");
                return null;
            }

            var id = Guid.NewGuid();
            var relative = Path.Combine("payloads", $"{id:N}.txt");
            var absolute = Path.Combine(_historyDir, relative);
            Directory.CreateDirectory(_payloadsDir);
            File.WriteAllBytes(absolute, bytes);

            var entry = new ClipboardEntry
            {
                Id = id,
                Kind = ClipboardEntryKind.Text,
                CreatedAt = DateTimeOffset.UtcNow,
                TextPreview = BuildPreview(text),
                PayloadFile = relative.Replace('\\', '/'),
                ByteSize = bytes.LongLength,
                ContentHash = hash
            };

            _index.Entries.Insert(0, entry);
            ApplyRetentionUnlocked();
            PersistIndexUnlocked();
            _logger.LogInformation("Entrée texte ajoutée ({Bytes} o, total={Count})", entry.ByteSize, _index.Entries.Count);
            raise = true;
            created = entry;
        }

        if (raise)
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public ClipboardEntry? AddImagePng(byte[] pngBytes, string? contentHash = null)
    {
        if (pngBytes is null || pngBytes.Length == 0)
            return null;

        var hash = contentHash ?? HashBytes(pngBytes);
        ClipboardEntry entry;

        lock (_gate)
        {
            if (IsConsecutiveDuplicate(hash))
            {
                _logger.LogDebug("Image ignorée (dédup consécutive)");
                return null;
            }

            var id = Guid.NewGuid();
            var relative = Path.Combine("payloads", $"{id:N}.png");
            var absolute = Path.Combine(_historyDir, relative);
            Directory.CreateDirectory(_payloadsDir);
            File.WriteAllBytes(absolute, pngBytes);

            entry = new ClipboardEntry
            {
                Id = id,
                Kind = ClipboardEntryKind.Image,
                CreatedAt = DateTimeOffset.UtcNow,
                TextPreview = "[Image]",
                PayloadFile = relative.Replace('\\', '/'),
                ByteSize = pngBytes.LongLength,
                ContentHash = hash
            };

            _index.Entries.Insert(0, entry);
            ApplyRetentionUnlocked();
            PersistIndexUnlocked();
            _logger.LogInformation("Entrée image ajoutée ({Bytes} o, total={Count})", entry.ByteSize, _index.Entries.Count);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public async Task<string?> ReadTextPayloadAsync(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardEntryKind.Text)
            return null;

        var path = GetAbsolutePayloadPath(entry);
        if (!File.Exists(path))
            return null;

        return await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false);
    }

    public string GetAbsolutePayloadPath(ClipboardEntry entry) =>
        Path.Combine(_historyDir, entry.PayloadFile.Replace('/', Path.DirectorySeparatorChar));

    public async Task<byte[]?> ReadImagePayloadAsync(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardEntryKind.Image)
            return null;

        var path = GetAbsolutePayloadPath(entry);
        if (!File.Exists(path))
            return null;

        return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    }

    public bool TryGet(Guid id, out ClipboardEntry? entry)
    {
        lock (_gate)
        {
            entry = _index.Entries.FirstOrDefault(e => e.Id == id);
            return entry is not null;
        }
    }

    public bool SetPinned(Guid id, bool pinned)
    {
        lock (_gate)
        {
            var e = _index.Entries.FirstOrDefault(x => x.Id == id);
            if (e is null)
                return false;
            e.IsPinned = pinned;
            PersistIndexUnlocked();
        }

        _logger.LogInformation("Épinglage appliqué : id={Id}, épinglé={Pinned}", id, pinned);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var e = _index.Entries.FirstOrDefault(x => x.Id == id);
            if (e is null)
                return false;
            DeleteEntryUnlocked(e);
            PersistIndexUnlocked();
        }

        _logger.LogInformation("Entrée supprimée : id={Id}", id);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int ClearUnpinned()
    {
        int removed;
        lock (_gate)
        {
            removed = RemoveWhere(e => !e.IsPinned);
            PersistIndexUnlocked();
        }

        _logger.LogInformation("Historique vidé : {Removed} entrée(s) non épinglée(s) supprimée(s)", removed);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public bool MatchesSearch(ClipboardEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        if (entry.TextPreview?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (entry.Kind == ClipboardEntryKind.Image)
            return "image".Contains(query, StringComparison.OrdinalIgnoreCase);

        try
        {
            var path = GetAbsolutePayloadPath(entry);
            if (!File.Exists(path))
                return false;

            var text = File.ReadAllText(path);
            return text.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Recherche payload ignorée");
            return false;
        }
    }

    public void ApplyRetention()
    {
        lock (_gate)
        {
            ApplyRetentionUnlocked();
            PersistIndexUnlocked();
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsConsecutiveDuplicate(string hash)
    {
        var newest = _index.Entries.FirstOrDefault();
        return newest?.ContentHash is not null
               && string.Equals(newest.ContentHash, hash, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRetentionUnlocked()
    {
        var s = _settings.Current;
        var removed = 0;

        switch (s.RetentionMode)
        {
            case RetentionMode.ByTime:
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, s.RetentionDays));
                removed += RemoveWhere(e => !e.IsPinned && e.CreatedAt < cutoff);
                break;
            }
            case RetentionMode.ByDiskSize:
            {
                var maxBytes = Math.Max(1, s.RetentionMaxMegabytes) * 1024L * 1024L;
                while (TotalBytesUnlocked() > maxBytes)
                {
                    var victim = _index.Entries.LastOrDefault(e => !e.IsPinned);
                    if (victim is null) break;
                    DeleteEntryUnlocked(victim);
                    removed++;
                }

                break;
            }
            case RetentionMode.ByEntryCount:
            default:
            {
                var max = Math.Max(1, s.RetentionMaxEntries);
                while (_index.Entries.Count > max)
                {
                    var victim = _index.Entries.LastOrDefault(e => !e.IsPinned);
                    if (victim is null) break;
                    DeleteEntryUnlocked(victim);
                    removed++;
                }

                break;
            }
        }

        if (removed > 0)
            _logger.LogInformation("Rétention : {Removed} entrée(s) purgée(s)", removed);
    }

    private int RemoveWhere(Func<ClipboardEntry, bool> predicate)
    {
        var toRemove = _index.Entries.Where(predicate).ToList();
        foreach (var e in toRemove)
            DeleteEntryUnlocked(e);
        return toRemove.Count;
    }

    private void DeleteEntryUnlocked(ClipboardEntry entry)
    {
        _index.Entries.Remove(entry);
        try
        {
            var path = GetAbsolutePayloadPath(entry);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Suppression payload ignorée");
        }
    }

    private long TotalBytesUnlocked() => _index.Entries.Sum(e => e.ByteSize);

    private void PersistIndexUnlocked()
    {
        try
        {
            Directory.CreateDirectory(_historyDir);
            var json = JsonSerializer.Serialize(_index, JsonOptions);
            var tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _indexPath, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            // Écriture atomique échouée : l'index en mémoire reste la référence (listelog §3.11)
            _logger.LogError(ex, "Écriture index.json échouée — historique en mémoire conservé");
        }
    }

    /// <summary>Sauvegarde d'un index corrompu avant réinitialisation (listelog §3.13).</summary>
    private void TryBackupCorruptIndex()
    {
        try
        {
            if (!File.Exists(_indexPath))
                return;

            var backupName = Path.GetFileNameWithoutExtension(_indexPath)
                + ".corrupt-"
                + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
                + Path.GetExtension(_indexPath);
            var backupPath = Path.Combine(_historyDir, backupName);
            File.Copy(_indexPath, backupPath, overwrite: true);
            _logger.LogWarning("Index corrompu sauvegardé sous {BackupName}", backupName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sauvegarde de l'index corrompu impossible");
        }
    }

    private static string BuildPreview(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length <= PreviewMaxChars)
            return flat;
        return flat[..PreviewMaxChars] + "…";
    }

    public static string HashBytes(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
