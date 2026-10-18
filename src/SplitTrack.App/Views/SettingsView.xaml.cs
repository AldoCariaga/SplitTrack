using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SplitTrack.App.ViewModels;
using SplitTrack.Engine.Input;

namespace SplitTrack.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnBrowseLibrary(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Where to save clips and recordings", InitialDirectory = vm.LibraryFolder };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.LibraryFolder = dialog.FolderName;
        }
    }

    /// <summary>Turns the pressed combination into hotkey text (e.g. Alt+F10); plain keys without a modifier are refused by the parser.</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.Tab or Key.Escape)
        {
            if (key is Key.Tab or Key.Escape)
            {
                e.Handled = false;
            }

            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        string keyName = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Scroll => "ScrollLock",
            _ => key.ToString(),
        };
        string text = string.Concat(
            modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : string.Empty,
            modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : string.Empty,
            modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : string.Empty,
            win ? "Win+" : string.Empty,
            keyName);

        try
        {
            string normalized = HotkeyGesture.Parse(text).ToString();
            if (box.Tag as string == "RecordHotkey")
            {
                vm.RecordHotkey = normalized;
            }
            else
            {
                vm.SaveClipHotkey = normalized;
            }

            box.ToolTip = null;
        }
        catch (FormatException ex)
        {
            box.ToolTip = ex.Message;
            ToolTipService.SetInitialShowDelay(box, 0);
            box.Text = $"{text}  (not usable)";
        }
    }

    private void OnClearRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.RecordHotkey = string.Empty;
        }
    }

    private void OnPickTarget(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrackEditor track } button)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button };
        if (track.Suggestions.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Nothing found; type the name instead", IsEnabled = false });
        }

        foreach (string suggestion in track.Suggestions)
        {
            var item = new MenuItem { Header = suggestion };
            item.Click += (_, _) => track.AddSuggestionCommand.Execute(suggestion);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }
}
