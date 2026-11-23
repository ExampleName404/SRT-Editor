using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SrtEditor;

public partial class TimecodeInput : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(TimecodeInput), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (sender, _) => ((TimecodeInput)sender).RefreshPartsUnlessTyping()));

    public static readonly DependencyProperty SplitProperty = DependencyProperty.Register(nameof(Split), typeof(bool),
        typeof(TimecodeInput), new PropertyMetadata(false, (sender, _) => ((TimecodeInput)sender).RefreshPresentation()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string),
        typeof(TimecodeInput), new PropertyMetadata(""));

    private static readonly Regex ClockParts = new(@"^([0-9]{1,13}):([0-9]{1,2}):([0-9]{1,2})[,.]([0-9]{1,3})$",
        RegexOptions.CultureInvariant);

    private readonly TextBox[] parts = [];
    private bool updatingParts;

    public TimecodeInput()
    {
        InitializeComponent();
        parts = [HoursInput, MinutesInput, SecondsInput, MillisecondsInput];
        foreach (var part in parts)
        {
            part.TextChanged += Part_TextChanged;
            part.LostKeyboardFocus += Part_LostKeyboardFocus;
            part.PreviewTextInput += Part_PreviewTextInput;
            DataObject.AddPastingHandler(part, Part_Pasting);
        }

        RefreshPresentation();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool Split
    {
        get => (bool)GetValue(SplitProperty);
        set => SetValue(SplitProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    internal void FocusEditor() => (SplitInputs.Visibility == Visibility.Visible ? SecondsInput : SingleInput).Focus();

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (e.NewFocus is not Visual next || !IsAncestorOf(next))
            RefreshPresentation();
    }

    private void RefreshPartsUnlessTyping()
    {
        if (!updatingParts && SingleInput?.IsKeyboardFocusWithin != true)
            RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        if (parts.Length == 0)
            return;

        string[]? values = Split ? SplitTime(Text) : null;
        SingleInput.Visibility = values is null ? Visibility.Visible : Visibility.Collapsed;
        SplitInputs.Visibility = values is null ? Visibility.Collapsed : Visibility.Visible;
        if (values is null)
            return;

        updatingParts = true;
        try
        {
            for (int index = 0; index < parts.Length; index++)
                parts[index].Text = values[index];
        }
        finally
        {
            updatingParts = false;
        }
    }

    private static string[]? SplitTime(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ["", "", "", ""];

        string clock = SubtitleTime.TryParse(input, out long milliseconds)
            ? SubtitleTime.Format(milliseconds) : input.Trim();
        var match = ClockParts.Match(clock);
        return match.Success ? [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value] : null;
    }

    private void Part_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingParts)
            return;

        string value = parts.All(part => part.Text.Length == 0) ? ""
            : $"{HoursInput.Text.PadLeft(2, '0')}:{MinutesInput.Text.PadLeft(2, '0')}:{SecondsInput.Text.PadLeft(2, '0')},{MillisecondsInput.Text.PadLeft(3, '0')}";

        updatingParts = true;
        try
        {
            SetCurrentValue(TextProperty, value);
        }
        finally
        {
            updatingParts = false;
        }
    }

    private void Part_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void Part_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var part = (TextBox)sender;
        if (part.Text.Length > 0)
            part.Text = part.Text.PadLeft(((string)part.Tag).Length, '0');
    }

    private void Part_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.All(char.IsAsciiDigit))
            e.CancelCommand();
    }

    private void TimeBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();
}
