using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SrtEditor.Tests;

internal static class AudioUiTests
{
    public static void Run()
    {
        var window = new MainWindow();
        try
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())
                ?? throw new InvalidOperationException("The test window has no presentation source.");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1240, 790));
            content.Arrange(new Rect(0, 0, 1240, 790));
            content.UpdateLayout();

            var panel = (AudioPanel)window.FindName("SoundPanel");
            var playback = (AudioPlayback)panel.DataContext;
            SetPlaybackReady(playback);

            SpaceRespectsFocusedControls(window, panel, playback, source);
            CancelEmptyPositionClearsError(panel, source);
        }
        finally
        {
            window.Close();
        }
    }

    private static void SpaceRespectsFocusedControls(MainWindow window, AudioPanel panel, AudioPlayback playback, HwndSource source)
    {
        var changes = FindCheckBox((DependencyObject)window.Content) ?? throw new InvalidOperationException("Preview checkbox was not found.");
        var disclosure = (Button)window.FindName("AudioDisclosure");
        var positionInput = (TextBox)panel.FindName("PositionInput");
        var slider = (Slider)panel.FindName("PositionSlider");

        Check.True(panel.Visibility == Visibility.Collapsed, "The audio panel starts collapsed.");
        Check.True(!RaisePreviewKey(changes, source, Key.Space) && !playback.IsPlaying, "Space on a checkbox reaches the native control.");
        Check.True(!RaisePreviewKey(disclosure, source, Key.Space) && !playback.IsPlaying, "Space on a button reaches the native control.");

        disclosure.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((FrameworkElement)window.Content).UpdateLayout();
        Check.True(!RaisePreviewKey(positionInput, source, Key.Space) && !playback.IsPlaying, "Space in an editable textbox remains available for input.");
        Check.True(RaisePreviewKey(slider, source, Key.Space) && playback.IsPlaying, "Space on the timeline starts playback.");
        Check.True(RaisePreviewKey(window, source, Key.Space) && !playback.IsPlaying, "Space in the window still pauses playback.");
    }

    private static void CancelEmptyPositionClearsError(AudioPanel panel, HwndSource source)
    {
        var input = (TextBox)panel.FindName("PositionInput");
        var readout = (TextBlock)panel.FindName("PositionReadout");
        var error = (TextBlock)panel.FindName("PositionError");
        var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(doubleClick, 2);
        readout.RaiseEvent(doubleClick);

        input.Clear();
        Check.True(RaisePreviewKey(input, source, Key.Enter) && error.Visibility == Visibility.Visible, "An empty seek time shows a validation error.");
        Check.True(RaisePreviewKey(input, source, Key.Escape) && input.Visibility == Visibility.Collapsed, "Escape closes the seek editor.");
        Check.True(error.Visibility == Visibility.Collapsed && input.ReadLocalValue(Control.BorderBrushProperty) == DependencyProperty.UnsetValue,
            "Closing the seek editor clears the validation message and border, even with an empty textbox.");
    }

    private static void SetPlaybackReady(AudioPlayback playback)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AudioPlayback).GetMethod("SetDuration", members)!.Invoke(playback, [20_000d]);
        var setState = typeof(AudioPlayback).GetMethod("SetState", members)!;
        setState.Invoke(playback, [Enum.Parse(setState.GetParameters()[0].ParameterType, "Ready")]);
    }

    private static bool RaisePreviewKey(UIElement element, HwndSource source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(args);
        return args.Handled;
    }

    private static CheckBox? FindCheckBox(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is CheckBox checkBox)
                return checkBox;
            if (FindCheckBox(child) is { } nested)
                return nested;
        }

        return null;
    }
}
