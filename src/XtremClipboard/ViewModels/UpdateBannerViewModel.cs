using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using XtremClipboard.Models;
using XtremClipboard.Services;

namespace XtremClipboard.ViewModels;

/// <summary>Bandeau mise à jour (MainWindow) — Règle auto-update.md / D-UPDATE.</summary>
public partial class UpdateBannerViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ILogger<UpdateBannerViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private UpdateCheckResult? _pending;
    private Task<string?>? _downloadTask;
    private int _prepareStarted;

    public UpdateBannerViewModel(IUpdateService updates, ILogger<UpdateBannerViewModel> logger)
    {
        _updates = updates;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("UpdateBannerViewModel doit être créé sur le thread UI.");
    }

    [ObservableProperty]
    private bool _isBannerOpen;

    [ObservableProperty]
    private string _bannerTitle = "Mise à jour disponible";

    [ObservableProperty]
    private string _bannerMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _bannerSeverity = InfoBarSeverity.Informational;

    /// <summary>True quand le Setup est en cache et hash OK — active « Mettre à jour ».</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isInstallReady;

    /// <summary>Check immédiat → bandeau, puis download en fond. Au démarrage, app configurée.</summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _prepareStarted, 1) == 1)
        {
            return;
        }

        try
        {
            var check = await _updates.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);
            if (check is null)
            {
                return;
            }

            _pending = check;
            var version = check.LatestVersion;

            // Bandeau dès la détection (pas après le download ~45 Mo).
            _dispatcher.TryEnqueue(() =>
            {
                BannerSeverity = InfoBarSeverity.Informational;
                BannerTitle = "Mise à jour disponible";
                BannerMessage = "Version " + version + " — téléchargement en cours…";
                IsInstallReady = false;
                IsBannerOpen = true;
            });

            _downloadTask = _updates.DownloadAsync(check, progress: null, cancellationToken);
            var path = await _downloadTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowError("Téléchargement impossible. Réessayez au prochain lancement.");
                return;
            }

            if (!await _updates.VerifyHashAsync(path, check.Sha256, cancellationToken).ConfigureAwait(false))
            {
                ShowError("Fichier de mise à jour invalide. Réessayez au prochain lancement.");
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                BannerSeverity = InfoBarSeverity.Informational;
                BannerTitle = "Mise à jour prête";
                BannerMessage = "La version " + version +
                    " est téléchargée. L'installation demandera l'autorisation Windows.";
                IsInstallReady = true;
                IsBannerOpen = true;
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Mise à jour : préparation annulée");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mise à jour : préparation");
            ShowError("Vérification de mise à jour interrompue.");
        }
    }

    private void ShowError(string message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            BannerSeverity = InfoBarSeverity.Error;
            BannerTitle = "Mise à jour impossible";
            BannerMessage = message;
            IsInstallReady = false;
            IsBannerOpen = true;
        });
    }

    [RelayCommand]
    private void Postpone()
    {
        IsBannerOpen = false;
        _logger.LogInformation("Mise à jour : repoussée jusqu'au prochain démarrage (fichier en cache)");
    }

    private bool CanApply() => IsInstallReady && _pending is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var pending = _pending;
        if (pending is null)
        {
            IsBannerOpen = false;
            return;
        }

        // Si l'utilisateur clique pendant le download : attendre la fin.
        if (!IsInstallReady && _downloadTask is not null)
        {
            try
            {
                var path = await _downloadTask.ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(path) ||
                    !await _updates.VerifyHashAsync(path, pending.Sha256).ConfigureAwait(true))
                {
                    ShowError("Téléchargement incomplet. Réessayez au prochain lancement.");
                    return;
                }

                IsInstallReady = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mise à jour : attente download avant install");
                ShowError("Téléchargement interrompu. Réessayez au prochain lancement.");
                return;
            }
        }

        var result = await _updates
            .InstallAndRestartAsync(pending.CachedInstallerPath, pending.Sha256)
            .ConfigureAwait(true);
        switch (result)
        {
            case UpdateInstallResult.UacCancelled:
                BannerSeverity = InfoBarSeverity.Informational;
                BannerTitle = "Mise à jour prête";
                BannerMessage = "Autorisation Windows refusée. Vous pourrez réessayer plus tard.";
                IsInstallReady = true;
                IsBannerOpen = true;
                break;
            case UpdateInstallResult.HashInvalid:
            case UpdateInstallResult.MissingInstaller:
            case UpdateInstallResult.Failed:
                BannerSeverity = InfoBarSeverity.Error;
                BannerTitle = "Mise à jour impossible";
                BannerMessage = "L'installation n'a pas pu démarrer. Réessayez au prochain lancement.";
                IsInstallReady = File.Exists(pending.CachedInstallerPath);
                IsBannerOpen = true;
                break;
            case UpdateInstallResult.Started:
                break;
        }
    }
}
