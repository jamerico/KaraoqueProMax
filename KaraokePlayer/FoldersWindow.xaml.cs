using System.Collections.ObjectModel;
using System.Windows;

namespace KaraokePlayer;

/// <summary>Edits the list of song folders.</summary>
public partial class FoldersWindow : Window
{
    readonly ObservableCollection<string> _folders;
    public List<string> Folders => [.. _folders];

    public FoldersWindow(IEnumerable<string> folders)
    {
        InitializeComponent();
        _folders = new ObservableCollection<string>(folders);
        List.ItemsSource = _folders;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha uma ou mais pastas com músicas (Ctrl para várias)", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames)
            if (!_folders.Contains(f, StringComparer.OrdinalIgnoreCase)) _folders.Add(f);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is string f) _folders.Remove(f);
    }

    void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
