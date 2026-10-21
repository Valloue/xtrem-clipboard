using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XtremClipboard.Models;

namespace XtremClipboard.Services;

/// <summary>
/// Persistance history/index.json + payloads (**D-STORE-LAYOUT**, **D-STORE-LIMITS**).
/// Ajoute la gestion OCR : file d'attente en arrière-plan, sortie payloads/{id}.ocr.txt (D-CLIP-PRIVACY).
/// </summary>
public sealed class ClipboardHistoryStore : IClipboardHistoryStore, IDisposable
{
    private const int PreviewMaxChars = 120;

    /// <summary>Limite OCR plan : pas d'OCR au-delà de 50 Mo (risque mémoire).</summary>
    private static readonly long MaxOcrBytes = 50 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ISettingsService _settings;
    private readonly IOcrService _ocr;
    private readonly ILogger<ClipboardHistoryStore> _logger;
    private readonly object _gate = new();
    private readonly object _cacheGate = new();
    private readonly string _historyDir;
    private readonly string _payloadsDir;
    private readonly string _indexPath;
    private readonly Channel<ClipboardEntry> _ocrQueue;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _ocrWorker;
    private HistoryIndex _index = new();

    /// <summary>
    /// Cache mémoire des textes (payload texte + OCR) — évite une lecture disque par frappe (revue §7).
    /// Publié UNE fois, puis muté uniquement sous <see cref="_cacheGate"/> : échange atomique,
    /// pas d'objet partagé mutable entre le build et les lecteurs (BUG-3 testeur).
    /// </summary>
    private Dictionary<Guid, string>? _searchTextCache;
    /// <summary>
    /// Journal des mises à jour arrivées PENDANT la construction du cache (OCR terminé,
    /// repli de recherche ciblé) : rejoué à la publication — aucune écriture perdue
    /// (l'ancien code no-op'ait quand le cache était nul → entrée introuvable jusqu'au
    /// redémarrage, BUG-3 testeur).
    /// </summary>
    private Dictionary<Guid, string>? _pendingSearchTexts;
    private HashSet<Guid>? _pendingRemovals;
    /// <summary>État du build du cache : 0 = non démarré, 1 = en cours, 2 = prêt (Interlocked).</summary>
    private int _cacheBuildState;
    /// <summary>Indique que la réconciliation OCR du démarrage a déjà eu lieu (pas de re-enqueue en boucle).</summary>
    private bool _ocrReconciled;

    public IReadOnlyList<ClipboardEntry> Entries
    {
        get
        {
            lock (_gate)
                return _index.Entries.ToList();
        }
    }

    public event EventHandler? HistoryChanged;

    public ClipboardHistoryStore(ISettingsService settings, IOcrService ocr, ILogger<ClipboardHistoryStore> logger)
    {
        _settings = settings;
        _ocr = ocr;
        _logger = logger;
        _historyDir = Path.Combine(settings.DataDirectory, "history");
        _payloadsDir = Path.Combine(_historyDir, "payloads");
        _indexPath = Path.Combine(_historyDir, "index.json");
        _ocrQueue = Channel.CreateUnbounded<ClipboardEntry>();
        _ocrWorker = Task.Run(ProcessOcrQueueAsync); // Démarrage du worker en arrière-plan
        // Drain best-effort à la fermeture du process : le worker finit l'entrée en cours ou s'arrête
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    /// <summary>
    /// Arrêt propre : ferme la file et laisse le worker finir l'entrée en cours (3 s max),
    /// puis annule. Une entrée interrompue reste Pending et sera reprise au prochain démarrage.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _ocrQueue.Writer.TryComplete();
            if (!_ocrWorker.Wait(TimeSpan.FromSeconds(3)))
            {
                _disposeCts.Cancel();
                _logger.LogWarning("Worker OCR non terminé après 3 s — abandon (reprise au prochain démarrage)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Arrêt du worker OCR : exception ignorée");
        }
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
                }
                else
                {
                    var json = File.ReadAllText(_indexPath);
                    _index = JsonSerializer.Deserialize<HistoryIndex>(json, JsonOptions) ?? new HistoryIndex();
                    _logger.LogInformation("Historique chargé : {Count} entrée(s)", _index.Entries.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec lecture index.json — index réinitialisé");
                TryBackupCorruptIndex();
                _index = new HistoryIndex();
            }

            ReconcileOcrUnlocked();
        }
    }

    /// <summary>
    /// Réconciliation OCR au chargement (revue §6) :
    /// - nettoyage des .ocr.txt orphelins (entrée disparue pendant un OCR) ;
    /// - reprise des entrées figées Pending et des échecs Failed (retry au démarrage — jamais de stall permanent) ;
    /// - backfill des images antérieures à l'OCR (statut None), dans la limite 50 Mo.
    /// Une seule fois par session : les retries ne tournent pas en boucle.
    /// </summary>
    private void ReconcileOcrUnlocked()
    {
        if (_ocrReconciled)
            return;
        _ocrReconciled = true;

        // 1) Orphelins : fichiers .ocr.txt sans entrée correspondante
        try
        {
            var known = _index.Entries
                .Where(e => e.OcrPayloadFile is not null)
                .Select(e => e.OcrPayloadFile!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_payloadsDir, "*.ocr.txt"))
            {
                var relative = "payloads/" + Path.GetFileName(file);
                if (!known.Contains(relative))
                {
                    File.Delete(file);
                    _logger.LogInformation("Fichier OCR orphelin supprimé : {File}", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nettoyage des fichiers OCR orphelins impossible");
        }

        // 2) Reprise + backfill
        var requeued = 0;
        foreach (var entry in _index.Entries.ToList())
        {
            if (entry.Kind != ClipboardEntryKind.Image)
                continue;

            // Payload OCR déclaré mais fichier absent → échec réessayable
            if (entry.OcrStatus == ClipboardOcrStatus.Completed && entry.OcrPayloadFile is not null
                && !File.Exists(Path.Combine(_historyDir, entry.OcrPayloadFile.Replace('/', Path.DirectorySeparatorChar))))
            {
                entry.OcrStatus = ClipboardOcrStatus.Failed;
                entry.OcrPayloadFile = null;
                _logger.LogWarning("Payload OCR manquant pour {Id} — remis en file (retry)", entry.Id);
            }

            var retryable = entry.OcrStatus is ClipboardOcrStatus.Pending or ClipboardOcrStatus.Failed;
            var backfill = entry.OcrStatus == ClipboardOcrStatus.None
                           && entry.ByteSize <= MaxOcrBytes
                           && File.Exists(GetAbsolutePayloadPath(entry));
            if (!retryable && !backfill)
                continue;

            entry.OcrStatus = ClipboardOcrStatus.Pending;
            entry.OcrPayloadFile = null;
            requeued++;
            if (_ocrQueue.Writer.TryWrite(entry))
                continue;
            entry.OcrStatus = ClipboardOcrStatus.Failed;
            requeued--;
        }

        if (requeued > 0)
        {
            PersistIndexUnlocked();
            _logger.LogInformation("OCR : {Count} image(s) remise(s) en file au démarrage (retry/backfill)", requeued);
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
            CacheSearchText(id, text);
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

        // Limite de sécurité plan : pas d'OCR au-delà de 50 Mo (risque mémoire)
        var tooLargeForOcr = pngBytes.Length > MaxOcrBytes;
        if (tooLargeForOcr)
        {
            _logger.LogWarning("Image trop grande pour OCR : {Size} octets — entrée conservée sans OCR", pngBytes.Length);
        }

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

        // Enfile l'entrée pour OCR en arrière-plan (jamais sur le thread UI — Step 4)
        if (!tooLargeForOcr)
            _ocrQueue.Writer.TryWrite(entry);

        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>
    /// Worker OCR : lit la file, exécute l'OCR, met à jour l'index et écrit le fichier .ocr.txt.
    /// Statuts honnêtes (revue §6) : un échec technique (moteur indisponible, décodage impossible,
    /// exception) marque <b>Failed</b> — retryable au prochain démarrage — et jamais « Completed ».
    /// </summary>
    private async Task ProcessOcrQueueAsync()
    {
        using var drainLinked = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        try
        {
            await foreach (var entry in _ocrQueue.Reader.ReadAllAsync(drainLinked.Token))
            {
                try
                {
                    var imageBytes = await ReadImagePayloadAsync(entry).ConfigureAwait(false);
                    if (imageBytes is null || imageBytes.Length == 0)
                    {
                        UpdateOcrStatus(entry.Id, ClipboardOcrStatus.Failed, null);
                        _logger.LogWarning("OCR impossible (id={Id}) — payload image absent", entry.Id);
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    var result = await _ocr.ExtractTextAsync(imageBytes).ConfigureAwait(false);
                    sw.Stop();

                    if (result.Outcome != OcrOutcome.Completed)
                    {
                        // Échec technique : statut distinct, jamais masqué en « Completed sans texte »
                        UpdateOcrStatus(entry.Id, ClipboardOcrStatus.Failed, null);
                        _logger.LogWarning("OCR échoué (id={Id}, statut={Status}, durée={Ms} ms) — retryable au prochain démarrage",
                            entry.Id, result.Outcome, sw.ElapsedMilliseconds);
                        continue;
                    }

                    // Entrée supprimée pendant l'OCR (rétention, suppression utilisateur) :
                    // ne pas écrire un .ocr.txt orphelin ni recréer de métadonnées fantômes.
                    if (!TryGet(entry.Id, out _))
                    {
                        _logger.LogInformation("OCR ignoré — entrée {Id} déjà supprimée", entry.Id);
                        continue;
                    }

                    var ocrRelPath = $"payloads/{entry.Id:N}.ocr.txt";
                    var ocrAbsPath = Path.Combine(_historyDir, ocrRelPath);

                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        // Succès sans texte détecté : statut Completed honnête, sans fichier
                        UpdateOcrStatus(entry.Id, ClipboardOcrStatus.Completed, null);
                        _logger.LogInformation("OCR terminé (id={Id}, durée={Ms} ms, sans texte)", entry.Id, sw.ElapsedMilliseconds);
                        continue;
                    }

                    await File.WriteAllTextAsync(ocrAbsPath, result.Text, Encoding.UTF8).ConfigureAwait(false);

                    // Garde de course : l'entrée peut avoir été supprimée pendant l'écriture → pas d'orphelin
                    if (!TryGet(entry.Id, out _))
                    {
                        try { File.Delete(ocrAbsPath); } catch (Exception ex) { _logger.LogDebug(ex, "Nettoyage .ocr.txt ignoré"); }
                        _logger.LogInformation("OCR ignoré — entrée {Id} supprimée pendant l'écriture", entry.Id);
                        continue;
                    }

                    UpdateOcrStatus(entry.Id, ClipboardOcrStatus.Completed, ocrRelPath);
                    CacheSearchText(entry.Id, result.Text);
                    _logger.LogInformation("OCR terminé (id={Id}, durée={Ms} ms, {Chars} chars)", entry.Id, sw.ElapsedMilliseconds, result.Text.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OCR pipeline échec (id={Id})", entry.Id);
                    UpdateOcrStatus(entry.Id, ClipboardOcrStatus.Failed, null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé (drain dépassé) : les entrées restées Pending seront reprises au démarrage suivant
        }
    }

    /// <summary>Mise à jour métadonnées OCR de l'entrée (statut, fichier) — persiste l'index.</summary>
    private void UpdateOcrStatus(Guid id, ClipboardOcrStatus status, string? ocrRelPath)
    {
        lock (_gate)
        {
            var entry = _index.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
                return;

            entry.OcrStatus = status;
            entry.OcrPayloadFile = ocrRelPath;
            PersistIndexUnlocked();
        }
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

    /// <summary>
    /// Recherche dans l'aperçu et dans les contenus texte/OCR — depuis le cache mémoire
    /// une fois prêt. Cette méthode ne déclenche JAMAIS de lecture disque lourde : tant
    /// que le cache complet n'est pas construit (build en arrière-plan, BUG-4 testeur),
    /// repli ciblé = lecture d'<b>un seul</b> payload pour l'entrée testée, mémoïsé pour
    /// ne jamais relire au coup suivant. L'overlay ne se fige plus à la première recherche.
    /// </summary>
    public bool MatchesSearch(ClipboardEntry entry, string query)
    {
        // Préchauffe non bloquante : le build complet part en arrière-plan au premier
        // besoin, jamais sur le thread UI (BUG-4 testeur).
        EnsureSearchCacheStarted();

        if (string.IsNullOrWhiteSpace(query))
            return true;

        if (entry.TextPreview?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        var cached = PeekCachedSearchText(entry.Id);
        if (cached is not null && cached.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        // Cache pas encore prêt (build en arrière-plan) : repli ciblé mémoïsé.
        if (Volatile.Read(ref _cacheBuildState) != CacheReady)
        {
            var text = TryReadSearchTextFor(entry);
            if (!string.IsNullOrEmpty(text))
            {
                CacheSearchText(entry.Id, text);
                if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (entry.Kind == ClipboardEntryKind.Image)
            return "image".Contains(query, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    /// <summary>État du build du cache recherche (valeurs de <see cref="_cacheBuildState"/>).</summary>
    private const int CacheNotStarted = 0;
    private const int CacheBuilding = 1;
    private const int CacheReady = 2;

    /// <summary>
    /// Démarre (une seule fois) la construction du cache en arrière-plan, de façon
    /// incrémentale : l'app reste fluide pendant le build, les recherches sont servies
    /// par le repli ciblé en attendant. Idempotent — sans attente bloquante.
    /// </summary>
    private void EnsureSearchCacheStarted()
    {
        if (Interlocked.CompareExchange(ref _cacheBuildState, CacheBuilding, CacheNotStarted) != CacheNotStarted)
            return;

        _logger.LogInformation("Construction du cache de recherche en arrière-plan…");
        _ = Task.Run(BuildSearchCacheAsync);
    }

    /// <summary>
    /// Build incrémental du cache : snapshot par <b>valeur</b> (id / chemins / statuts —
    /// plus d'objets entry partagés mutables entre le worker OCR et le build, BUG-3),
    /// lecture des payloads hors verrou par lots avec points de reprise, publication
    /// atomique en fusionnant le journal des mises à jour arrivées pendant le build.
    /// </summary>
    private async Task BuildSearchCacheAsync()
    {
        var cache = new Dictionary<Guid, string>();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            List<SearchSource> sources;
            lock (_gate)
            {
                sources = _index.Entries
                    .Select(SourceFor)
                    .Where(s => s is not null)
                    .Select(s => s!.Value)
                    .ToList();
            }

            foreach (var source in sources)
            {
                var text = SafeReadText(source.RelativePath);
                if (!string.IsNullOrEmpty(text))
                    cache[source.Id] = text;

                // Incrémental : on rend la main entre chaque lot pour ne pas monopoliser
                // un thread plusieurs secondes (historique volumineux).
                if (cache.Count % 32 == 0)
                    await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build du cache de recherche interrompu — repli ciblé conservé");
            Interlocked.Exchange(ref _cacheBuildState, CacheNotStarted);
            return;
        }

        lock (_cacheGate)
        {
            // Rejoue le journal : tout ce qui a changé pendant le build (OCR terminé,
            // ajout, suppression) prime sur le snapshot lu au départ.
            if (_pendingSearchTexts is { Count: > 0 })
                foreach (var (id, text) in _pendingSearchTexts)
                    cache[id] = text;
            if (_pendingRemovals is { Count: > 0 })
                foreach (var id in _pendingRemovals)
                    cache.Remove(id);
            _pendingSearchTexts = null;
            _pendingRemovals = null;
            _searchTextCache = cache; // publication atomique (référence immuable ensuite)
            _cacheBuildState = CacheReady;
        }

        stopwatch.Stop();
        _logger.LogInformation("Cache de recherche prêt : {Count} entrée(s) en {DurationMs} ms (arrière-plan)",
            cache.Count, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Source recherchable copiée par valeur (plus d'objet entry partagé).</summary>
    private readonly record struct SearchSource(Guid Id, string RelativePath);

    private static SearchSource? SourceFor(ClipboardEntry entry) =>
        entry.Kind switch
        {
            ClipboardEntryKind.Text when entry.PayloadFile is not null
                => new SearchSource(entry.Id, entry.PayloadFile),
            ClipboardEntryKind.Image when entry.OcrStatus == ClipboardOcrStatus.Completed
                                          && entry.OcrPayloadFile is not null
                => new SearchSource(entry.Id, entry.OcrPayloadFile),
            _ => null
        };

    /// <summary>Repli ciblé : lit le payload d'<b>une seule</b> entrée (état relu sous verrou).</summary>
    private string? TryReadSearchTextFor(ClipboardEntry entry)
    {
        SearchSource? source;
        lock (_gate)
            source = SourceFor(entry);
        return source is { } s ? SafeReadText(s.RelativePath) : null;
    }

    private string? SafeReadText(string relativePath)
    {
        if (relativePath is null)
            return null;
        var path = Path.Combine(_historyDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (Exception ex)
        {
            // Fichier verrouillé/effacé entre-temps : repli sans crash, sans contenu loggé
            _logger.LogDebug(ex, "Lecture payload recherche ignorée");
            return null;
        }
    }

    private string? PeekCachedSearchText(Guid id)
    {
        lock (_cacheGate)
        {
            if (_searchTextCache is { } cache)
                return cache.TryGetValue(id, out var text) ? text : null;

            // Cache pas encore publié : le journal fait partie de l'état effectif du
            // cache (un texte mémoïsé en repli ou une fin d'OCR y sont déjà visibles).
            return _pendingSearchTexts is { } pending && pending.TryGetValue(id, out var pendingText)
                ? pendingText
                : null;
        }
    }

    /// <summary>
    /// Ajoute/met à jour le texte recherchable d'une entrée : dans le cache s'il est
    /// publié, sinon dans le journal rejoué à la publication — plus aucune écriture
    /// perdue quand un OCR se termine pendant le build (BUG-3 testeur).
    /// </summary>
    private void CacheSearchText(Guid id, string text)
    {
        lock (_cacheGate)
        {
            if (_searchTextCache is { } cache)
                cache[id] = text;
            else
                (_pendingSearchTexts ??= new Dictionary<Guid, string>())[id] = text;
            _pendingRemovals?.Remove(id);
        }
    }

    /// <summary>
    /// Retire une entrée du cache (suppression / rétention) : direct si publié, sinon
    /// journalisée pour être appliquée à la publication du build en cours.
    /// </summary>
    private void UncacheSearchText(Guid id)
    {
        lock (_cacheGate)
        {
            if (_searchTextCache is { } cache)
                cache.Remove(id);
            else
                (_pendingRemovals ??= new HashSet<Guid>()).Add(id);
            _pendingSearchTexts?.Remove(id);
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
        UncacheSearchText(entry.Id);
        try
        {
            var path = GetAbsolutePayloadPath(entry);
            if (File.Exists(path))
                File.Delete(path);

            // Suppression du fichier OCR associé (Step 6 — cohérence des fichiers)
            if (entry.OcrPayloadFile is not null)
            {
                var ocrPath = Path.Combine(_historyDir, entry.OcrPayloadFile.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(ocrPath))
                    File.Delete(ocrPath);
            }
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
