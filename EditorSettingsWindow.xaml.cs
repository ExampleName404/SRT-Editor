using System.Windows;

namespace SrtEditor;

public partial class EditorSettingsWindow : Window
{
    private readonly EditorViewModel editor;

    internal EditorSettingsWindow(EditorViewModel editor)
    {
        this.editor = editor;
        InitializeComponent();
        CacheFolder.Text = editor.CachePath;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(this,
            "Удалить локальные версии всех SRT из истории редактора?",
            "Очистка истории", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            int count = editor.ClearVersions();
            CacheStatus.Text = $"Удалено версий: {count}.";
        }
        catch (Exception exception) when (EditorViewModel.IsCacheError(exception))
        {
            CacheStatus.Text = $"Не удалось очистить всю историю. {exception.Message}";
        }
    }
}
