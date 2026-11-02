using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SplitTrack.App.ViewModels;

namespace SplitTrack.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(RecorderViewModel recorder, LibraryViewModel library, SettingsViewModel settings)
    {
        Recorder = recorder;
        Library = library;
        Settings = settings;
        DataContext = this;
        InitializeComponent();
    }

    public RecorderViewModel Recorder { get; }

    public LibraryViewModel Library { get; }

    public SettingsViewModel Settings { get; }

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double click on a card's quick-action button is not an open.
        var source = e.OriginalSource as DependencyObject;
        if (source.FindAncestor<System.Windows.Controls.Primitives.ButtonBase>() is null
            && source.FindAncestor<ListBoxItem>()?.DataContext is LibraryEntry entry)
        {
            Library.Open(entry);
        }
    }

    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (EntryList.SelectedItem is not LibraryEntry entry)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            Library.Open(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            Library.DeleteCommand.Execute(entry);
            e.Handled = true;
        }
    }
}

internal static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            // Text runs are not visuals; they only have a logical parent.
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
