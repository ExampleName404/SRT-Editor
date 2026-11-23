using System.Windows;
using System.Windows.Controls;

namespace SrtEditor;

public partial class VersionHistoryWindow : Window
{
    internal VersionHistoryWindow(string fileName, IReadOnlyList<FileVersion> versions)
    {
        InitializeComponent();
        DocumentName.Text = fileName;
        DocumentName.ToolTip = fileName;
        VersionList.ItemsSource = versions;
        if (versions.Count > 0)
            VersionList.SelectedIndex = 0;
        else
            EmptyHint.Visibility = Visibility.Visible;
    }

    internal FileVersion? SelectedVersion => VersionList.SelectedItem as FileVersion;
    internal bool RestoreSelected { get; private set; }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedVersion;
        CompareButton.IsEnabled = RestoreButton.IsEnabled = selected is not null;
        VersionPreview.Text = selected is null ? "" : SrtFile.Render(selected.Cues, "\r\n");
        VersionPreview.ScrollToHome();
    }

    private void Compare_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        RestoreSelected = true;
        DialogResult = true;
    }
}
