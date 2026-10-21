using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace XtremClipboard.Services;

/// <summary>
/// Service OCR <b>PP-OCRv6 Medium</b> via ONNX Runtime (modèles ONNX officiels PaddlePaddle,
/// Apache-2.0 — décision utilisateur explicite, voir D-OCR-ENGINE) :
/// - détection  : <c>Models/OCR/PP-OCRv6_medium_det/inference.onnx</c>
///   (https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx, sha256 eb13b44b…d086e1)
/// - reconnaissance : <c>Models/OCR/PP-OCRv6_medium_rec/inference.onnx</c>
///   (https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx, sha256 9c09abf0…1b673ba)
/// - dictionnaire : section <c>PostProcess.character_dict</c> du
///   <c>inference.yml</c> officiel du dépôt rec (18 708 entrées, 50 langues → FR + EN couverts).
/// Modèles embarqués à côté de l'exe (Setup — D-OCR-LOCAL) : aucun téléchargement au runtime.
/// Le texte OCR est du contenu clipboard : jamais journalisé en clair (D-CLIP-PRIVACY).
/// Pré/post-traitement conformes aux <c>inference.yml</c> officiels et aux métadonnées
/// ONNX réelles : det = DB (normalisation ImageNet sur BGR, thresh 0.2, box_thresh 0.45,
/// unclip 1.4) ; rec = entrée 3×48×W BGR (hauteur 48, largeur DYNAMIQUE alimentée à
/// ceil(48·w/h), pad droit normalisé si l'export impose une largeur fixe),
/// normalisation 0.5/0.5, décodage CTC greedy.
/// </summary>
public sealed class OcrService : IOcrService
{
    /// <summary>Score minimal des lignes OCR retenues (règle plan : ignorer &lt; 0.5).</summary>
    private const float MinConfidence = 0.5f;

    // ---- Constantes du pipeline det (issu de inference.yml officiel du modèle det) ----
    private const float DetThresh = 0.2f;          // DBPostProcess.thresh
    private const float DetBoxThresh = 0.45f;      // DBPostProcess.box_thresh
    private const float DetUnclipRatio = 1.4f;     // DBPostProcess.unclip_ratio
    private const int DetMaxCandidates = 3000;     // DBPostProcess.max_candidates
    private const int DetLimitSideLen = 736;       // DetResizeForTest (limit_type=min)
    private const int DetLimitMaxSide = 4000;      // borne haute (plage TRT du yml)
    private const int DetDivisible = 32;           // dims det multiples de 32

    // ---- Constantes du pipeline rec (issu de inference.yml officiel du modèle rec) ----
    private const int RecHeight = 48;              // image_shape [3, 48, W]
    // Plage largeur déclarée par le modèle : axe DYNAMIQUE (métadonnées ONNX
    // [-1,3,48,-1], yml trt_dynamic_shapes max 3200). Le contenu est alimenté à
    // largeur exacte ceil(48·w/h) — medium = modèle précision, jamais écrasé à
    // 320 (BUG-2 testeur) ; 3200 = borne haute déclarée du modèle.
    private const int RecMaxWidth = 3200;

    /// <summary>Chemin du dictionnaire de reconnaissance (extrait du yml officiel).</summary>
    private static readonly string DetModelPath = Path.Combine(
        AppContext.BaseDirectory, "Models", "OCR", "PP-OCRv6_medium_det", "inference.onnx");
    private static readonly string RecModelPath = Path.Combine(
        AppContext.BaseDirectory, "Models", "OCR", "PP-OCRv6_medium_rec", "inference.onnx");
    private static readonly string RecDictPath = Path.Combine(
        AppContext.BaseDirectory, "Models", "OCR", "PP-OCRv6_medium_rec", "inference.yml");

    private readonly ILogger<OcrService> _logger;
    private readonly object _initLock = new();

    private InferenceSession? _detSession;
    private InferenceSession? _recSession;
    private string[] _recVocabulary = [];
    private string _detInputName = "x";
    private string _recInputName = "x";
    private string _recOutputName = string.Empty;
    /// <summary>Largeur rec imposée par l'export ONNX (0 = axe largeur dynamique).</summary>
    private int _recFixedWidth;
    private volatile bool _initialized;

    /// <summary>Indique si le moteur est prêt (modèles chargés).</summary>
    public bool IsInitialized => _initialized;

    public OcrService(ILogger<OcrService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extrait le texte OCR d'une image PNG en arrière-plan (jamais le thread UI).
    /// Retourne un statut d'échec distinct en cas de problème technique — jamais
    /// « Completed » à tort (revue §6). Le texte lui-même n'est jamais loggé.
    /// </summary>
    public async Task<OcrResult> ExtractTextAsync(byte[] imageData)
    {
        if (imageData is null || imageData.Length == 0)
            return OcrResult.Empty(OcrOutcome.DecodeFailed);

        return await Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (!EnsureInitialized())
                    return OcrResult.Empty(OcrOutcome.EngineUnavailable);

                // 1) Décodage PNG → plan BGR (WinRT, sans dépendance OpenCV)
                var image = await DecodePngToBgrAsync(imageData).ConfigureAwait(false);
                if (image is null)
                {
                    _logger.LogWarning("Image OCR illisible (décodage vide, {Size} octets)", imageData.Length);
                    return OcrResult.Empty(OcrOutcome.DecodeFailed);
                }

                // 2) Détection : prob map DB → boîtes texte
                var boxes = RunDetection(image);
                if (boxes.Count == 0)
                {
                    sw.Stop();
                    _logger.LogInformation("OCR terminé : 0 région, {DurationMs} ms, {Size} octets en entrée",
                        sw.ElapsedMilliseconds, imageData.Length);
                    return OcrResult.Empty(OcrOutcome.Completed);
                }

                // 3) Reconnaissance ligne par ligne + décodage CTC
                var lines = new List<string>(boxes.Count);
                foreach (var box in boxes)
                {
                    var (text, score) = RunRecognition(image, box);
                    if (score < MinConfidence || text.Length == 0)
                        continue;
                    lines.Add(text);
                }

                sw.Stop();
                _logger.LogInformation("OCR terminé : {RegionCount} région(s) retenue(s) / {Detected} détectée(s), {DurationMs} ms, {Size} octets en entrée",
                    lines.Count, boxes.Count, sw.ElapsedMilliseconds, imageData.Length);

                // Le texte lui-même n'apparaît JAMAIS dans le journal (D-CLIP-PRIVACY)
                return new OcrResult(OcrOutcome.Completed, string.Join('\n', lines));
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "OCR échoué après {DurationMs} ms ({Size} octets en entrée)",
                    sw.ElapsedMilliseconds, imageData.Length);
                return OcrResult.Empty(OcrOutcome.Failed);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Initialisation paresseuse thread-safe : les deux modèles ONNX et le dictionnaire
    /// sont chargés au premier usage, pas au démarrage de l'app.
    /// </summary>
    private bool EnsureInitialized()
    {
        if (_initialized && _detSession is not null && _recSession is not null)
            return true;

        lock (_initLock)
        {
            if (_initialized && _detSession is not null && _recSession is not null)
                return true;

            var sw = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(DetModelPath) || !File.Exists(RecModelPath) || !File.Exists(RecDictPath))
                {
                    _logger.LogError(
                        "Fichiers OCR manquants : det présent={Det}, rec présent={Rec}, dict présent={Dict} — OCR indisponible",
                        File.Exists(DetModelPath), File.Exists(RecModelPath), File.Exists(RecDictPath));
                    return false;
                }

                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    // Worker de fond unique : ne pas saturer les cœurs de la machine
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
                };

                var det = new InferenceSession(DetModelPath, options);
                var rec = new InferenceSession(RecModelPath, new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
                });

                var vocabulary = LoadRecVocabulary(RecDictPath);
                if (vocabulary.Count == 0)
                {
                    det.Dispose();
                    rec.Dispose();
                    _logger.LogError("Dictionnaire OCR vide ({Path}) — OCR indisponible", RecDictPath);
                    return false;
                }

                _detSession = det;
                _recSession = rec;
                _recVocabulary = vocabulary.ToArray();
                // Noms d'entrées/sorties résolus dynamiquement (noms internes paddle2onnx variables)
                _detInputName = det.InputMetadata.Keys.First();
                _recInputName = rec.InputMetadata.Keys.First();
                _recOutputName = SelectRecOutput(rec, _recVocabulary.Length + 2);
                // Spéc réelle du modèle : largeur rec dynamique (dims[-1] == -1) ou fixe
                // (export figé). Pilotage du padding dans BuildRecInput.
                var recDims = rec.InputMetadata[_recInputName].Dimensions;
                _recFixedWidth = recDims is { Length: 4 } && recDims[3] > 0 ? recDims[3] : 0;
                _logger.LogInformation(
                    "Spéc entrée rec : dims=[{Dims}] — largeur {WidthMode}",
                    string.Join(",", recDims ?? []),
                    _recFixedWidth > 0 ? $"fixe {_recFixedWidth}" : "dynamique");

                _initialized = true;
                sw.Stop();
                _logger.LogInformation(
                    "Moteur OCR prêt : PP-OCRv6 Medium ONNX (det {DetMb} Mo, rec {RecMb} Mo, {Vocab} caractères, init en {DurationMs} ms)",
                    new FileInfo(DetModelPath).Length / (1024 * 1024),
                    new FileInfo(RecModelPath).Length / (1024 * 1024),
                    _recVocabulary.Length, sw.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Dégradé maîtrisé : l'app continue sans OCR, nouvelle tentative au prochain appel
                _logger.LogError(ex, "Échec init moteur OCR après {DurationMs} ms — OCR indisponible pour cette session",
                    sw.ElapsedMilliseconds);
                return false;
            }
        }
    }

    /// <summary>
    /// Choisit la sortie rec exploitable : tenseur rang 3 dont le dernier axe vaut
    /// classes attendues (vocab + blank + espace), sinon premier tenseur rang 3.
    /// </summary>
    private static string SelectRecOutput(InferenceSession rec, int expectedClasses)
    {
        string? rank3 = null;
        foreach (var (name, meta) in rec.OutputMetadata)
        {
            if (!meta.IsTensor)
                continue;
            var dimensions = meta.Dimensions;
            if (dimensions is { Length: 3 })
            {
                if (dimensions[2] == expectedClasses)
                    return name;
                rank3 ??= name;
            }
        }

        if (rank3 is not null)
            return rank3;
        return rec.OutputMetadata.Keys.First();
    }

    /// <summary>
    /// Extrait le dictionnaire de la section PostProcess.character_dict du inference.yml
    /// officiel (YAML simple : une entrée par ligne «  - 'car' », quotes simples).
    /// La clé character_dict est indentée sous PostProcess → comparaison après TrimStart.
    /// </summary>
    private static List<string> LoadRecVocabulary(string path)
    {
        var vocabulary = new List<string>(20000);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var inDict = false;
        foreach (var raw in lines)
        {
            var trimmedLine = raw.TrimStart();
            if (!inDict)
            {
                if (trimmedLine.StartsWith("character_dict:", StringComparison.Ordinal))
                    inDict = true;
                continue;
            }

            if (!trimmedLine.StartsWith("- ", StringComparison.Ordinal))
                break; // fin de la liste
            vocabulary.Add(UnquoteYamlScalar(trimmedLine[2..].Trim()));
        }

        return vocabulary;
    }

    /// <summary>Déquote un scalaire YAML simple-quote : <c>''</c> → <c>'</c>.</summary>
    private static string UnquoteYamlScalar(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        return value;
    }

    /// <summary>
    /// Décode un PNG en plan BGR (HWC). Images dépassant <see cref="DetLimitMaxSide"/> :
    /// mise à l'échelle au décodage (BitmapTransform) pour borner la mémoire.
    /// Retourne null si le décodage échoue.
    /// </summary>
    private async Task<BgrImage?> DecodePngToBgrAsync(byte[] pngBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(pngBytes.AsBuffer()).AsTask().ConfigureAwait(false);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
            var transform = new BitmapTransform();
            // Plafonne la mémoire décodée pour les images géantes (64 Mo de pixels max)
            var maxSide = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            var scale = 1.0;
            if (maxSide > DetLimitMaxSide)
                scale = (double)DetLimitMaxSide / maxSide;
            var width = Math.Max(1, (int)Math.Round(decoder.OrientedPixelWidth * scale));
            var height = Math.Max(1, (int)Math.Round(decoder.OrientedPixelHeight * scale));
            transform.ScaledWidth = (uint)width;
            transform.ScaledHeight = (uint)height;

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);

            var bgra = pixelData.DetachPixelData();
            if (bgra.Length < width * height * 4)
                return null;

            var bgr = new byte[width * height * 3];
            for (int i = 0, s = 0, d = 0; i < width * height; i++, s += 4, d += 3)
            {
                // BGRA → BGR (l'ordre BGR est celui attendu par DecodeImage img_mode=BGR)
                bgr[d] = bgra[s];
                bgr[d + 1] = bgra[s + 1];
                bgr[d + 2] = bgra[s + 2];
            }

            return new BgrImage(width, height, bgr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Décodage image impossible ({Size} octets) {EventCode} {Category} {Result}",
                pngBytes.Length,
                "CLIP-OCR-DECODE",
                "error",
                "echec");
            return null;
        }
    }

    // ------------------------------------------------------------------
    //  Détection (PP-OCRv6_medium_det + post-traitement DB)
    // ------------------------------------------------------------------

    /// <summary>Boîte texte en coordonnées image décodée (axis-aligned).</summary>
    private readonly record struct TextBox(int X, int Y, int Width, int Height);

    private sealed record BgrImage(int Width, int Height, byte[] Pixels);

    /// <summary>
    /// Pré-traitement det officiel (DetResizeForTest limit_type=min 736, dims multiples de 32,
    /// normalisation ImageNet sur BGR), puis inférence et extraction des boîtes DB.
    /// </summary>
    private List<TextBox> RunDetection(BgrImage image)
    {
        var (input, rh, rw) = BuildDetInput(image);
        var session = _detSession!;
        var inputTensor = NamedOnnxValue.CreateFromTensor(_detInputName, input);
        using var results = session.Run([inputTensor]);
        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions; // [1, 1, H, W] attendu
        var probHeight = dims[^2];
        var probWidth = dims[^1];
        var probabilities = output.ToArray();

        return ExtractDbBoxes(probabilities, probWidth, probHeight, rh, rw);
    }

    /// <summary>Construit le tenseur det [1,3,H,W] normalisé (retourne aussi les ratios d'échelle).</summary>
    private static (DenseTensor<float> Tensor, float RatioH, float RatioW) BuildDetInput(BgrImage image)
    {
        var h0 = image.Height;
        var w0 = image.Width;

        // DetResizeForTest (limit_type=min) : agrandit si le petit côté est sous la limite
        var ratio = 1.0;
        if (Math.Min(h0, w0) < DetLimitSideLen)
            ratio = (double)DetLimitSideLen / Math.Min(h0, w0);
        // Borne haute (plage dynamic shapes du yml)
        if (Math.Max(h0, w0) * ratio > DetLimitMaxSide)
            ratio = (double)DetLimitMaxSide / Math.Max(h0, w0);

        var resizeH = Math.Max((int)Math.Round(h0 * ratio / DetDivisible) * DetDivisible, DetDivisible);
        var resizeW = Math.Max((int)Math.Round(w0 * ratio / DetDivisible) * DetDivisible, DetDivisible);
        var resized = ResizeBgrBilinear(image, resizeW, resizeH);

        var tensor = new DenseTensor<float>([1, 3, resizeH, resizeW]);
        NormalizeToTensor(resized, tensor, resizeW, meanB: 0.485f, meanG: 0.456f, meanR: 0.406f,
            invStdB: 1f / 0.229f, invStdG: 1f / 0.224f, invStdR: 1f / 0.225f);

        return (tensor, resizeH / (float)h0, resizeW / (float)w0);
    }

    /// <summary>
    /// Post-traitement DB : binarisation (thresh 0.2), composantes connexes 8-connexité,
    /// score boîte (moyenne de la prob map sur la bbox, box_thresh 0.45), unclip rectangulaire
    /// (offset = aire × unclip_ratio / périmètre), remappage vers l'image décodée.
    /// </summary>
    private static List<TextBox> ExtractDbBoxes(
        float[] probabilities, int probWidth, int probHeight, float ratioH, float ratioW)
    {
        var boxes = new List<TextBox>(64);
        var visited = new bool[probWidth * probHeight];
        var stack = new Stack<int>();

        for (var start = 0; start < visited.Length; start++)
        {
            if (visited[start] || probabilities[start] <= DetThresh)
                continue;

            // Flood fill itératif de la composante connexe
            stack.Clear();
            stack.Push(start);
            visited[start] = true;
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var y = index / probWidth;
                var x = index % probWidth;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= probHeight)
                        continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= probWidth)
                            continue;
                        var neighbor = ny * probWidth + nx;
                        if (!visited[neighbor] && probabilities[neighbor] > DetThresh)
                        {
                            visited[neighbor] = true;
                            stack.Push(neighbor);
                        }
                    }
                }
            }

            if (boxes.Count >= DetMaxCandidates)
                break;

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            if (Math.Min(bw, bh) < 3)
                continue;

            // Score boîte : moyenne de la prob map sur la bbox (comme DBPostProcess.box_score)
            var sum = 0f;
            for (var y = minY; y <= maxY; y++)
            {
                var row = y * probWidth;
                for (var x = minX; x <= maxX; x++)
                    sum += probabilities[row + x];
            }
            var score = sum / (bw * bh);
            if (score < DetBoxThresh)
                continue;

            // Unclip rectangulaire : offset = aire × unclip_ratio / périmètre
            var distance = bw * bh * DetUnclipRatio / (2f * (bw + bh));
            var x0 = Math.Max(0, (int)Math.Floor(minX - distance));
            var y0 = Math.Max(0, (int)Math.Floor(minY - distance));
            var x1 = Math.Min(probWidth - 1, (int)Math.Ceiling(maxX + distance));
            var y1 = Math.Min(probHeight - 1, (int)Math.Ceiling(maxY + distance));
            if (x1 - x0 < 1 || y1 - y0 < 1)
                continue;

            // Remappage det → image décodée (coords / ratio)
            var ox = (int)Math.Round(x0 / ratioW);
            var oy = (int)Math.Round(y0 / ratioH);
            var ow = (int)Math.Round((x1 - x0 + 1) / ratioW);
            var oh = (int)Math.Round((y1 - y0 + 1) / ratioH);
            ow = Math.Clamp(ow, 1, probWidth);
            oh = Math.Clamp(oh, 1, probHeight);
            boxes.Add(new TextBox(ox, oy, ow, oh));
        }

        // Ordre de lecture naturel : haut → bas, gauche → droite
        boxes.Sort(static (a, b) =>
        {
            var by = a.Y.CompareTo(b.Y);
            return by != 0 ? by : a.X.CompareTo(b.X);
        });
        return boxes;
    }

    // ------------------------------------------------------------------
    //  Reconnaissance (PP-OCRv6_medium_rec + CTC greedy)
    // ------------------------------------------------------------------

    /// <summary>Reconnaît une boîte : crop BGR, resize 48×W, CTC. Retourne (texte, confiance).</summary>
    private (string Text, float Score) RunRecognition(BgrImage image, TextBox box)
    {
        var session = _recSession!;
        var tensor = BuildRecInput(image, box);
        var input = NamedOnnxValue.CreateFromTensor(_recInputName, tensor);
        using var results = session.Run([input]);
        var output = results.First(o => o.Name == _recOutputName).AsTensor<float>();
        return DecodeCtc(output, _recVocabulary);
    }

    /// <summary>
    /// Construit l'entrée rec (C=3, H=48, largeur W) pour une boîte détectée.
    /// Prétraitement standard PaddleOCR, conforme à la spéc réelle du modèle :
    /// - hauteur fixe 48, redimensionnement ratio-préservé → largeur contenu
    ///   <c>ceil(48·w/h)</c> <b>sans écrasement</b> (l'ancien cap à 320 rendait
    ///   les lignes longues illisibles — BUG-2 testeur) ;
    /// - l'axe largeur est <b>dynamique</b> côté modèle (inference.yml :
    ///   trt_dynamic_shapes 160–3200) → on alimente la largeur exacte du contenu,
    ///   bornée à la plage déclarée du modèle ;
    /// - si l'export ONNX impose une largeur FIXE (métadonnées de l'entrée) :
    ///   resize ratio-préservé plafonné puis <b>padding droit effectif</b> (0 en
    ///   espace normalisé) — le tenseur alloué et le buffer restent alignés, plus
    ///   aucun dérapage d'index (BUG-1 testeur : IndexOutOfRangeException sur
    ///   toute ligne de ratio &lt; 6,67).
    /// </summary>
    private DenseTensor<float> BuildRecInput(BgrImage image, TextBox box)
    {
        var x = Math.Clamp(box.X, 0, image.Width - 1);
        var y = Math.Clamp(box.Y, 0, image.Height - 1);
        var w = Math.Clamp(box.Width, 1, image.Width - x);
        var h = Math.Clamp(box.Height, 1, image.Height - y);

        // Largeur du contenu une fois porté à h=48 (ratio préservé)
        var contentW = (int)MathF.Ceiling(RecHeight * (float)w / h);
        contentW = Math.Clamp(contentW, 1, RecMaxWidth);

        // Largeur du tenseur : largeur fixe du modèle si l'export l'impose,
        // sinon la largeur exacte du contenu (axe dynamique du yml).
        var targetW = _recFixedWidth > 0 ? _recFixedWidth : contentW;

        // Buffer réellement couvert par le resize (≤ tenseur) : le reste = pad droit
        var resizedW = Math.Min(contentW, targetW);

        var resized = ResizeBgrBilinear(image, resizedW, RecHeight, cropX: x, cropY: y, cropW: w, cropH: h);
        var tensor = new DenseTensor<float>([1, 3, RecHeight, targetW]);
        NormalizeToTensor(resized, tensor, resizedW, meanB: 0.5f, meanG: 0.5f, meanR: 0.5f,
            invStdB: 2f, invStdG: 2f, invStdR: 2f);
        return tensor;
    }

    /// <summary>
    /// Décodage CTC greedy (convention PaddleOCR : index 0 = blank, dernier index = espace) ;
    /// score = moyenne des probabilités retenues.
    /// </summary>
    private static (string Text, float Score) DecodeCtc(Tensor<float> output, string[] vocabulary)
    {
        var dims = output.Dimensions; // [1, T, C]
        var steps = dims[^2];
        var classes = dims[^1];
        var spaceClass = vocabulary.Length + 1; // convention CTC : 0 = blank, dernier = espace

        var characters = new List<string>(steps);
        var scores = new List<float>(steps);
        var previous = -1;

        for (var t = 0; t < steps; t++)
        {
            var best = 0;
            var bestLogit = float.NegativeInfinity;
            for (var c = 0; c < classes; c++)
            {
                var value = output[0, t, c];
                if (value > bestLogit)
                {
                    bestLogit = value;
                    best = c;
                }
            }

            if (best == previous || best == 0)
            {
                previous = best;
                continue;
            }
            previous = best;

            // Confiance : les exports ONNX PaddleOCR incluent déjà le softmax final
            // (valeurs dans [0,1]) — softmax manuel seulement si ce n'est pas le cas
            var bestValue = output[0, t, best];
            var probability = bestValue;
            if (bestValue is < 0f or > 1.0001f)
            {
                var maxLogit = bestValue;
                for (var c = 0; c < classes; c++)
                {
                    var value = output[0, t, c];
                    if (value > maxLogit)
                        maxLogit = value;
                }
                var sumExp = 0f;
                for (var c = 0; c < classes; c++)
                    sumExp += MathF.Exp(output[0, t, c] - maxLogit);
                probability = MathF.Exp(bestLogit - maxLogit) / Math.Max(sumExp, 1e-6f);
            }

            var character = best == spaceClass
                ? " "
                : vocabulary[best - 1];
            if (character.Length == 0)
                continue;
            characters.Add(character);
            scores.Add(probability);
        }

        if (characters.Count == 0)
            return (string.Empty, 0f);

        var builder = new StringBuilder(characters.Count);
        foreach (var character in characters)
            builder.Append(character);
        return (builder.ToString(), scores.Average());
    }

    // ------------------------------------------------------------------
    //  Outils image (sans dépendance OpenCV)
    // ------------------------------------------------------------------

    /// <summary>
    /// Normalise un plan BGR HWC (0..255) dans un tenseur CHW : (v/255 − mean) / std.
    /// Les valeurs mean/std passées en paramètres s'appliquent dans l'ordre B, G, R.
    /// <paramref name="contentColumns"/> = largeur réelle du buffer <paramref name="bgr"/> ;
    /// les colonnes du tenseur au-delà restent à 0 (pad droit officiel PaddleOCR :
    /// 0 en espace normalisé = gris moyen 127,5). Buffer et tenseur ne peuvent plus se
    /// désaligner — c'est la cause du crash d'origine (BUG-1 testeur).
    /// </summary>
    private static void NormalizeToTensor(
        byte[] bgr, DenseTensor<float> tensor, int contentColumns,
        float meanB, float meanG, float meanR,
        float invStdB, float invStdG, float invStdR)
    {
        var height = tensor.Dimensions[2];
        var width = tensor.Dimensions[3];
        var contentSpan = Math.Clamp(contentColumns, 0, width);
        var means = new[] { meanB, meanG, meanR };
        var invStds = new[] { invStdB, invStdG, invStdR };

        for (var c = 0; c < 3; c++)
        {
            var mean = means[c];
            var invStd = invStds[c];
            var channelOffset = c * height * width;
            for (var row = 0; row < height; row++)
            {
                var destRow = channelOffset + row * width;
                var srcRow = row * contentSpan * 3;
                for (var col = 0; col < contentSpan; col++)
                    tensor.Buffer.Span[destRow + col] =
                        (bgr[srcRow + col * 3 + c] / 255f - mean) * invStd;
            }
        }
    }

    /// <summary>
    /// Redimensionnement bilinéaire BGR. Sans crop : toute l'image. Avec crop : extrait
    /// d'abord la zone (cropX/cropY/cropW/cropH) puis redimensionne vers destW×destH.
    /// </summary>
    private static byte[] ResizeBgrBilinear(
        BgrImage source, int destW, int destH,
        int cropX = 0, int cropY = 0, int cropW = 0, int cropH = 0)
    {
        if (cropW <= 0 || cropH <= 0)
        {
            cropX = 0;
            cropY = 0;
            cropW = source.Width;
            cropH = source.Height;
        }

        var destination = new byte[destW * destH * 3];
        var scaleX = cropW / (float)destW;
        var scaleY = cropH / (float)destH;

        for (var dy = 0; dy < destH; dy++)
        {
            var srcY = cropY + (dy + 0.5f) * scaleY - 0.5f;
            var y0 = (int)MathF.Floor(srcY);
            var fracY = srcY - y0;
            y0 = Math.Clamp(y0, cropY, cropY + cropH - 1);
            var y1 = Math.Clamp(y0 + 1, cropY, cropY + cropH - 1);

            for (var dx = 0; dx < destW; dx++)
            {
                var srcX = cropX + (dx + 0.5f) * scaleX - 0.5f;
                var x0 = (int)MathF.Floor(srcX);
                var fracX = srcX - x0;
                x0 = Math.Clamp(x0, cropX, cropX + cropW - 1);
                var x1 = Math.Clamp(x0 + 1, cropX, cropX + cropW - 1);

                var i00 = (y0 * source.Width + x0) * 3;
                var i10 = (y0 * source.Width + x1) * 3;
                var i01 = (y1 * source.Width + x0) * 3;
                var i11 = (y1 * source.Width + x1) * 3;
                var destIndex = (dy * destW + dx) * 3;

                for (var c = 0; c < 3; c++)
                {
                    var top = source.Pixels[i00 + c] * (1 - fracX) + source.Pixels[i10 + c] * fracX;
                    var bottom = source.Pixels[i01 + c] * (1 - fracX) + source.Pixels[i11 + c] * fracX;
                    destination[destIndex + c] = (byte)Math.Clamp((int)MathF.Round(top * (1 - fracY) + bottom * fracY), 0, 255);
                }
            }
        }

        return destination;
    }
}
