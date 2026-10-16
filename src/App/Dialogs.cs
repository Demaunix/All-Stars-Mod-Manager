using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shape = System.Windows.Shapes;

namespace AllStarsModManager.App;

public enum DialogKind { Info, Question, Warning }

/// <summary>
/// Shared frame of the small code-built dialogs: a finish-line strip, a body and a button bar on a darker footer.
/// Keeps the themed look in one place (the XAML windows use the same brushes).
/// </summary>
static class DialogFrame
{
    public static Brush B(string key) => ThemedWindow.Res<Brush>(key);

    public static Grid Build(UIElement body, StackPanel buttons)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new Border { Height = 0 });
        Grid.SetRow(body, 1); grid.Children.Add(body);
        var footer = new Border { Background = B("PanelBrush"), BorderBrush = B("LineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12), Child = buttons };
        Grid.SetRow(footer, 2); grid.Children.Add(footer);
        return grid;
    }

    public static StackPanel ButtonRow() => new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    public static Shape.Path Icon(DialogKind kind) => new()
    {
        Data = ThemedWindow.Res<Geometry>(kind == DialogKind.Warning ? "IconWarn" : "IconInfo"),
        Style = ThemedWindow.Res<Style>("IconStroke"),
        Stroke = B(kind == DialogKind.Warning ? "WarnBrush" : kind == DialogKind.Question ? "AccentTextBrush" : "InfoBrush"),
        Width = 26, Height = 26, StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 16, 0)
    };
}

/// <summary>A themed replacement for the system message box: same wording and buttons, same keyboard behaviour (Enter = default, Esc = cancel).</summary>
public sealed class MessageDialog : ThemedWindow
{
    public bool Yes { get; private set; }

    MessageDialog(string title, string message, bool askYesNo, DialogKind kind)
    {
        Title = title; Width = 520; MinWidth = 380; MaxHeight = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "MessageWindow");

        var body = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(DialogFrame.Icon(kind));
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = Res<Style>("PageTitle"), FontSize = 15, TextWrapping = TextWrapping.Wrap });
        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = DialogFrame.B("TextBrush") };
        AutomationProperties.SetAutomationId(msg, "MessageText");
        text.Children.Add(new ScrollViewer { Content = msg, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(text, 1); body.Children.Add(text);

        var buttons = DialogFrame.ButtonRow();
        if (askYesNo)
        {
            var no = new Button { Content = "No", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            AutomationProperties.SetAutomationId(no, "MessageNo");
            var yes = new Button { Content = "Yes", MinWidth = 90, IsDefault = true, Style = Res<Style>("PrimaryButton") };
            AutomationProperties.SetAutomationId(yes, "MessageYes");
            yes.Click += (_, _) => { Yes = true; DialogResult = true; };
            buttons.Children.Add(no); buttons.Children.Add(yes);
            Loaded += (_, _) => yes.Focus();
        }
        else
        {
            var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, IsCancel = true, Style = Res<Style>("PrimaryButton") };
            AutomationProperties.SetAutomationId(ok, "MessageOk");
            ok.Click += (_, _) => { DialogResult = true; };
            buttons.Children.Add(ok);
            Loaded += (_, _) => ok.Focus();
        }
        Content = DialogFrame.Build(body, buttons);
    }

    static Window? OwnerOrNull(Window? owner) => owner is { IsLoaded: true, IsVisible: true } ? owner : null;

    /// <summary>Yes/No question. Returns true only for Yes.</summary>
    public static bool Ask(Window? owner, string title, string message)
    {
        var d = new MessageDialog(title, message, true, DialogKind.Question);
        if (OwnerOrNull(owner) is { } o) d.Owner = o; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d.Yes;
    }

    /// <summary>One-button notice.</summary>
    public static void Tell(Window? owner, string title, string message, DialogKind kind = DialogKind.Info)
    {
        var d = new MessageDialog(title, message, false, kind);
        if (OwnerOrNull(owner) is { } o) d.Owner = o; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
    }
}

/// <summary>Small code-built dialogs (a name prompt and a plain-text editor).</summary>
public sealed class PromptWindow : ThemedWindow
{
    readonly TextBox _box = new();
    public string? Result { get; private set; }

    public PromptWindow(string title, string message, string? initial)
    {
        Title = title; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "PromptWindow");
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = title, Style = Res<Style>("PageTitle"), FontSize = 15 });
        root.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 8, 0, 10), TextWrapping = TextWrapping.Wrap, Foreground = DialogFrame.B("MutedBrush") });
        _box.Text = initial ?? ""; _box.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_box, "PromptBox");
        AutomationProperties.SetName(_box, title);
        root.Children.Add(_box);
        var buttons = DialogFrame.ButtonRow();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, Style = Res<Style>("PrimaryButton") };
        AutomationProperties.SetAutomationId(ok, "PromptOk");
        ok.Click += (_, _) => { Result = _box.Text; DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        Content = DialogFrame.Build(root, buttons);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string message, string? initial)
    {
        var w = new PromptWindow(title, message, initial) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }
}

public sealed class TextEditWindow : ThemedWindow
{
    readonly TextBox _box = new();
    public string? Result { get; private set; }

    public TextEditWindow(string title, string note, string text)
    {
        Title = title; Width = 780; Height = 600; MinWidth = 520; MinHeight = 380; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "TextEditWindow");
        var body = new Grid { Margin = new Thickness(22, 18, 22, 14) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = DialogFrame.B("MutedBrush") });
        _box.Text = text; _box.AcceptsReturn = true; _box.AcceptsTab = true; _box.FontFamily = Res<FontFamily>("MonoFont"); _box.FontSize = 13;
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _box.VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAutomationId(_box, "TextEditBox");
        AutomationProperties.SetName(_box, title);
        Grid.SetRow(_box, 1); body.Children.Add(_box);
        var buttons = DialogFrame.ButtonRow();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new Button { Content = "Continue to save…", MinWidth = 150, Style = Res<Style>("PrimaryButton") };
        ok.Click += (_, _) => { Result = _box.Text; DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        Content = DialogFrame.Build(body, buttons);
    }

    public static string? Edit(Window owner, string title, string note, string text)
    {
        var w = new TextEditWindow(title, note, text) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }
}
