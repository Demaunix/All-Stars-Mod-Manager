using System.Windows;
using AllStarsModManager.Transactions;

namespace AllStarsModManager.App;

/// <summary>
/// The one place changes are confirmed. It first checks the files (with a real byte counter), then shows exactly what
/// will change, what is blocking, and anything worth knowing; only then can the user apply.
/// </summary>
public partial class ApplyWindow : ThemedWindow
{
    readonly MainViewModel _vm;
    readonly Func<IProgress<long>?, CancellationToken, PlanResult> _prepare;
    readonly CancellationTokenSource _cts = new();
    PlanResult? _plan;
    bool _working, _done;
    long _bytes;

    public bool Applied { get; private set; }

    public ApplyWindow(MainViewModel vm, Func<IProgress<long>?, CancellationToken, PlanResult> prepare, string title)
    {
        InitializeComponent();
        _vm = vm; _prepare = prepare;
        Title = title; TitleText.Text = title;
        Loaded += async (_, _) => await CheckAsync();
        Closing += (_, e) =>
        {
            if (_working && !_done) { e.Cancel = true; RequestCancel(); }
        };
    }

    async Task CheckAsync()
    {
        _working = true;
        using var hold = _vm.Exclusive("Checking files…");
        StatusText.Text = "Checking every file that matters before anything is changed…";
        var progress = new Progress<long>(n => { _bytes += n; StatusText.Text = $"Checking every file that matters before anything is changed… {Fmt.Bytes(_bytes)} read"; });
        try
        {
            _plan = await Task.Run(() => _prepare(progress, _cts.Token));
        }
        catch (OperationCanceledException) { _working = false; _done = true; Close(); return; }
        catch (Exception e)
        {
            _working = false; Bar.Visibility = Visibility.Collapsed;
            StatusText.Text = "The check could not finish: " + e.Message;
            CancelButton.Content = "Close";
            return;
        }
        _working = false;
        Bar.Visibility = Visibility.Collapsed;
        Show(_plan);
    }

    void Show(PlanResult plan)
    {
        var blockers = plan.Blockers;
        BlockerList.ItemsSource = blockers.Select(b => new NoteVM { Title = "", Text = b, Kind = ChipKind.Danger }).ToList();
        BlockerPanel.Visibility = blockers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangeList.ItemsSource = plan.Plan.PreviewLines;
        ChangePanel.Visibility = plan.Plan.PreviewLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var notes = new List<NoteVM>();
        foreach (var n in plan.Notes) notes.Add(new NoteVM { Title = "", Text = n, Kind = ChipKind.Info });
        foreach (var w in plan.Warnings) notes.Add(new NoteVM { Title = "", Text = w, Kind = ChipKind.Warn });
        NoteList.ItemsSource = notes;
        NotePanel.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (blockers.Count > 0) { StatusText.Text = "The game's loader would refuse this combination, so nothing has been changed and Apply is switched off."; ApplyButton.IsEnabled = false; CancelButton.Content = "Close"; }
        else if (!plan.HasChanges) { StatusText.Text = "There is nothing to change."; CancelButton.Content = "Close"; }
        else { StatusText.Text = "Everything checks out. Review the list, then apply. You can cancel while it runs; anything already done is undone."; ApplyButton.IsEnabled = true; }
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        ApplyButton.IsEnabled = false; _working = true; Bar.Visibility = Visibility.Visible;
        CancelButton.Content = "Cancel";
        LogPanel.Visibility = Visibility.Visible; LogText.Text = "";
        StatusText.Text = "Applying…";
        var log = new List<string>();
        var progress = new Progress<string>(s => { log.Add(s); LogText.Text = string.Join("\n", log.TakeLast(12)); });
        ApplyResult r;
        using (_vm.Exclusive("Applying changes…"))
        {
            try { r = await Task.Run(() => _vm.Svc.Apply(_plan, _cts.Token, progress)); }
            catch (Exception ex) { r = new ApplyResult { Declined = true, Message = ex.Message }; }
        }
        _working = false; _done = true; Bar.Visibility = Visibility.Collapsed;
        Applied = r.Success;
        CancelButton.Content = "Close";
        if (r.Success) { StatusText.Text = "Done. Your mods folder now matches your choice."; }
        else if (r.NeedsAttention) StatusText.Text = r.Message;
        else if (r.Cancelled) StatusText.Text = "Cancelled. Everything was put back as it was.";
        else StatusText.Text = r.Message;
        if (!r.Success)
        {
            var more = r.Details.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (more.Count > 0) { LogPanel.Visibility = Visibility.Visible; LogText.Text = string.Join("\n", more.Take(12)); }
        }
    }

    void RequestCancel()
    {
        StatusText.Text = "Cancelling… finishing the current step and putting everything back.";
        _cts.Cancel();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_working && !_done) { RequestCancel(); return; }
        Close();
    }
}
