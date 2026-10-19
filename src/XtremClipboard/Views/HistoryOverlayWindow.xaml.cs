using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using XtremClipboard.Services;
using XtremClipboard.ViewModels;

namespace XtremClipboard.Views;

public sealed partial class HistoryOverlayWindow : Window
{
    public HistoryViewModel ViewModel { get; }

    public HistoryOverlayWindow(HistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Xtrem Clipboard";
        AppIcon.Apply(AppWindow);
        WindowTheme.Track(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => ViewModel.CloseCommand.Execute(null);

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                ViewModel.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ViewModel.MoveSelection(-1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case VirtualKey.Down:
                ViewModel.MoveSelection(1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (ViewModel.SelectedItem is not null)
                    ViewModel.PasteCommand.Execute(ViewModel.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void ScrollSelectionIntoView()
    {
        if (ViewModel.SelectedItem is not null)
            EntriesList.ScrollIntoView(ViewModel.SelectedItem);
    }

    private void EntriesList_ItemClick(object sender, Microsoft.UI.Xaml.Controls.ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItemViewModel item)
            ViewModel.SelectedItem = item;
    }

    private void EntriesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not null)
            ViewModel.PasteCommand.Execute(ViewModel.SelectedItem);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItemViewModel item })
            ViewModel.PasteCommand.Execute(item);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItemViewModel item })
            ViewModel.CopyCommand.Execute(item);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItemViewModel item })
            ViewModel.TogglePinCommand.Execute(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItemViewModel item })
            ViewModel.DeleteCommand.Execute(item);
    }
}
