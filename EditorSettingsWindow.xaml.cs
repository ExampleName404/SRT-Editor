using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace SrtEditor;

public partial class EditorSettingsWindow : Window
{
    private readonly EditorViewModel editor;

    internal EditorSettingsWindow(EditorViewModel editor)
    {
        this.editor = editor;
        InitializeComponent();
        CacheFolder.Text = editor.CachePath;
        var assembly = typeof(App).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3);
        ReleaseVersion.Text = $"Версия {version}";
    }

    private void GitHub_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception exception)
        {
            MessageBox.Show(this, $"Не удалось открыть ссылку.\n\n{exception.Message}", "GitHub",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
