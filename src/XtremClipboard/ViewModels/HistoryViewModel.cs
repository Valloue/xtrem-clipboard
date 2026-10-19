using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using XtremClipboard.Services;

namespace XtremClipboard.ViewModels;

/// <summary>
/// Liste historique pour l'overlay (**D-UI-OVERLAY-TRAY**).
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly IClipboardHistoryStore _store;
    private readonly IPasteService _paste;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    /// <summary>Assigné par HistoryOverlayService pour fermer la popup.</summary>
    public Action? RequestClose { get; set; }

    /// <summary>Assigné au démarrage pour ouvrir la fenêtre Réglages.</summary>
    public Action? RequestOpenSettings { get; set; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private HistoryItemViewModel? _selectedItem;

    public HistoryViewModel(
        IClipboardHistoryStore store,
        IPasteService paste,
        ILogger<HistoryViewModel> logger)
    {
        _store = store;
        _paste = paste;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _store.HistoryChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    public void Refresh()
    {
        var q = SearchText?.Trim() ?? string.Empty;
        var list = _store.Entries
            .Where(e => _store.MatchesSearch(e, q))
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.CreatedAt)
            .Select(e => new HistoryItemViewModel(e, _store))
            .ToList();

        Items.Clear();
        foreach (var item in list)
            Items.Add(item);

        StatusText = Items.Count == 0
            ? "Aucune entrée — copiez du texte ou une image."
            : $"{Items.Count} entrée(s) · ↑↓ Entrée pour coller";
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
            return;

        var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        if (index < 0)
            index = delta > 0 ? -1 : 0;

        index = Math.Clamp(index + delta, 0, Items.Count - 1);
        SelectedItem = Items[index];
        _logger.LogDebug("Sélection déplacée ({Index}/{Count})", index + 1, Items.Count);
    }

    [RelayCommand]
    private async Task PasteAsync(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        _logger.LogInformation("Coller demandé ({Kind}, {Bytes} o)", item.Entry.Kind, item.Entry.ByteSize);
        try
        {
            RequestClose?.Invoke();
            await _paste.PasteAsync(item.Entry).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec collage");
            StatusText = "Échec du collage.";
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _logger.LogInformation("Réglages demandés (engrenage overlay)");
        RequestClose?.Invoke();
        RequestOpenSettings?.Invoke();
    }

    [RelayCommand]
    private async Task CopyAsync(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        _logger.LogInformation("Copier demandé ({Kind}, {Bytes} o)", item.Entry.Kind, item.Entry.ByteSize);
        try
        {
            await _paste.CopyToClipboardAsync(item.Entry).ConfigureAwait(true);
            StatusText = "Copié dans le presse-papiers.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec copie");
            StatusText = "Échec de la copie.";
        }
    }

    [RelayCommand]
    private void TogglePin(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        var next = !item.IsPinned;
        if (_store.SetPinned(item.Id, next))
            item.IsPinned = next;
    }

    [RelayCommand]
    private void Delete(HistoryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        _logger.LogInformation("Supprimer demandé (id={Id})", item.Id);
        _store.Delete(item.Id);
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
