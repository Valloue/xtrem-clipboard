using CommunityToolkit.Mvvm.ComponentModel;
using XtremClipboard.Models;
using XtremClipboard.Services;

namespace XtremClipboard.ViewModels;

public partial class HistoryItemViewModel : ObservableObject
{
    private readonly IClipboardHistoryStore _store;

    public ClipboardEntry Entry { get; }

    public Guid Id => Entry.Id;

    public bool IsImage => Entry.Kind == ClipboardEntryKind.Image;

    public bool IsText => Entry.Kind == ClipboardEntryKind.Text;

    public string Preview => IsText ? Entry.TextPreview ?? string.Empty : string.Empty;

    public string TimeLabel => Entry.CreatedAt.ToLocalTime().ToString("HH:mm");

    public string? ImagePath { get; }

    public Uri? ThumbnailUri { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool _isPinned;

    public string PinLabel => IsPinned ? "Désépingler" : "Épingler";

    public HistoryItemViewModel(ClipboardEntry entry, IClipboardHistoryStore store)
    {
        Entry = entry;
        _store = store;
        IsPinned = entry.IsPinned;
        if (entry.Kind == ClipboardEntryKind.Image)
        {
            ImagePath = store.GetAbsolutePayloadPath(entry);
            ThumbnailUri = new Uri(ImagePath, UriKind.Absolute);
        }
    }
}
