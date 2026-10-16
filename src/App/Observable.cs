using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AllStarsModManager.App;

public abstract class Obs : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class RelayCommand : ICommand
{
    readonly Action<object?> _run;
    readonly Func<object?, bool>? _can;
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can()) { }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public enum ChipKind { Muted, Ok, Warn, Danger, Info }

public sealed record ChipVM(string Text, ChipKind Kind, string? Tip = null);

public enum BannerKind { Info, Warn, Danger }

public sealed class BannerVM
{
    public string Id { get; init; } = "";
    public string Text { get; set; } = "";
    public BannerKind Kind { get; init; }
    public string? ActionText { get; set; }
    public ICommand? Action { get; set; }
    public string? SecondActionText { get; set; }
    public ICommand? SecondAction { get; set; }
    public bool HasAction => Action is not null;
    public bool HasSecondAction => SecondAction is not null;
    public string AutomationId => "Banner_" + Id;
}
public sealed class NoteVM
{
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public ChipKind Kind { get; init; }
}

public static class Fmt
{
    public static string Bytes(long n) =>
        n >= 1L << 30 ? $"{n / (double)(1L << 30):0.0} GB" : n >= 1L << 20 ? $"{n / (double)(1L << 20):0.0} MB" : n >= 1L << 10 ? $"{n / (double)(1L << 10):0.0} KB" : $"{n} bytes";
}
