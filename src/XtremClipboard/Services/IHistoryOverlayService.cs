namespace XtremClipboard.Services;

public interface IHistoryOverlayService
{
    bool IsVisible { get; }

    void Show();

    void Hide();

    void Toggle();
}
