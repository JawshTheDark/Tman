using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public sealed class TweakItem : ObservableObject
{
    readonly Action<TweakItem, bool> _change;

    public TweakItem(Tweak tweak, Action<TweakItem, bool> change)
    {
        Tweak = tweak;
        _change = change;
    }

    public Tweak Tweak { get; }
    public string Name => Tweak.Name;
    public string About => Tweak.About;
    public string Group => Tweak.Group.ToUpperInvariant();

    TweakState _state = TweakState.Unknown;
    bool _changed;

    public TweakState State => _state;
    public bool Changed => _changed;

    /// <summary>The checkbox: null for a partly applied tweak. Setting it applies the change.</summary>
    public bool? IsOn
    {
        get => _state switch { TweakState.On => true, TweakState.Off => false, _ => null };
        set
        {
            if (value is { } on && CanChange) _change(this, on);
        }
    }

    public bool CanChange => _state is TweakState.On or TweakState.Off or TweakState.Partly && (!Tweak.NeedsAdmin || MainWindow.IsAdmin);

    public string? LockReason =>
        _state == TweakState.Locked ? "Set by a policy on this PC, so Tman leaves it alone."
        : _state == TweakState.Unknown ? "Tman couldn't read this setting."
        : Tweak.NeedsAdmin && !MainWindow.IsAdmin ? "Needs administrator rights. Use File > Restart as administrator."
        : null;

    public IEnumerable<string> Tags
    {
        get
        {
            if (_state == TweakState.Locked) yield return "Set by policy";
            if (_state == TweakState.Partly) yield return "Partly on";
            if (_changed) yield return "Changed by Tman";
            if (Tweak.NeedsAdmin) yield return "Admin";
            if (Tweak.Effect == TweakEffect.Explorer) yield return "Explorer restart";
            if (Tweak.Effect == TweakEffect.SignOut) yield return "Sign out";
            if (Tweak.Effect == TweakEffect.Restart) yield return "Restart";
        }
    }

    public void Update(TweakState state, bool changed)
    {
        _state = state;
        _changed = changed;
        Raise(nameof(IsOn));
        Raise(nameof(CanChange));
        Raise(nameof(LockReason));
        Raise(nameof(Tags));
    }
}

public partial class TweaksPage : UserControl, IPage
{
    readonly List<TweakItem> _items;
    readonly ICollectionView _view;
    readonly HashSet<TweakEffect> _pending = new();
    string _filter = "";
    bool _busy;

    public TweaksPage()
    {
        InitializeComponent();
        _items = Tweaks.All.Select(t => new TweakItem(t, (item, on) => Change(item, on))).ToList();
        foreach (var group in _items.Select(i => i.Tweak.Group).Distinct()) GroupFilter.Items.Add(group);
        _view = new CollectionViewSource { Source = _items }.View;
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakItem.Group)));
        _view.Filter = Matches;
        List.ItemsSource = _view;
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
            UpdateCount();
        };
    }

    public async void OnShown() => await Reload();
    public void FocusSearch() => Search.FocusBox();

    bool Matches(object o)
    {
        var item = (TweakItem)o;
        var show = ShowFilter?.SelectedIndex ?? 0;
        if (show == 1 && item.State != TweakState.On) return false;
        if (show == 2 && !item.Changed) return false;
        if (GroupFilter.SelectedIndex > 0 && !Equals(GroupFilter.SelectedItem, item.Tweak.Group)) return false;
        return _filter.Length == 0 || Ui.Contains(item.Name, _filter) || Ui.Contains(item.About, _filter) || Ui.Contains(item.Group, _filter);
    }

    void ShowFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view == null) return;
        _view.Refresh();
        UpdateCount();
    }

    void UpdateCount()
    {
        int on = _items.Count(i => i.State == TweakState.On), changed = _items.Count(i => i.Changed);
        CountText.Text = $"{on} of {_items.Count} on · {changed} changed by Tman";
        UndoButton.IsEnabled = changed > 0 && !_busy;
    }

    async Task Reload()
    {
        var states = await Task.Run(() => _items.Select(i => (Item: i, State: Tweaks.Read(i.Tweak), Changed: Tweaks.HasUndo(i.Tweak))).ToList());
        foreach (var (item, state, changed) in states) item.Update(state, changed);
        // Rebuilding every row is slow, and only these filters depend on a tweak's state.
        if (ShowFilter.SelectedIndex != 0) _view.Refresh();
        UpdateCount();
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        List.IsEnabled = !busy;
        MinimalButton.IsEnabled = StandardButton.IsEnabled = AdvancedButton.IsEnabled = ExportButton.IsEnabled = ImportButton.IsEnabled = !busy;
        UndoButton.IsEnabled = !busy && _items.Any(i => i.Changed);
    }

    /// <summary>Applies a list of changes in order, stopping nothing on failure, and reports what went wrong at the end.</summary>
    async Task Run(IReadOnlyList<(TweakItem Item, bool On)> changes)
    {
        SetBusy(true);
        var errors = new List<string>();
        int done = 0;
        foreach (var (item, on) in changes)
        {
            CountText.Text = $"Applying {++done} of {changes.Count}…";
            var error = await Task.Run(() => Tweaks.Set(item.Tweak, on));
            if (error != null) errors.Add($"{item.Name}: {error}");
            else if (item.Tweak.Effect != TweakEffect.None) _pending.Add(item.Tweak.Effect);
        }
        await Task.Run(Tweaks.Broadcast);
        SetBusy(false);
        await Reload();
        UpdatePending();
        if (errors.Count > 0) Ui.Error("Some tweaks couldn't be changed:\n\n" + string.Join("\n", errors));
    }

    async void Change(TweakItem item, bool on)
    {
        if (_busy) return;
        await Run(new[] { (item, on) });
    }

    void UpdatePending()
    {
        var parts = new List<string>();
        if (_pending.Contains(TweakEffect.Explorer)) parts.Add("Some changes appear after Explorer restarts.");
        if (_pending.Contains(TweakEffect.SignOut)) parts.Add("Some need you to sign out.");
        if (_pending.Contains(TweakEffect.Restart)) parts.Add("Some need a PC restart.");
        PendingText.Text = string.Join(" ", parts);
        PendingBar.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExplorerButton.Visibility = _pending.Contains(TweakEffect.Explorer) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Turns on a set of tweaks after confirming the exact list, with a restore point first when possible.</summary>
    async Task ApplySet(IEnumerable<TweakItem> wanted, string label)
    {
        var offNow = wanted.Where(i => i.State != TweakState.On).ToList();
        var skipped = offNow.Where(i => !i.CanChange).ToList();
        var run = offNow.Except(skipped).ToList();
        if (run.Count == 0)
        {
            Ui.Error(offNow.Count == 0 ? $"All of {label} is already on." : $"Nothing can be applied: all {skipped.Count} remaining tweaks need administrator rights or are locked.");
            return;
        }

        var footer = skipped.Count > 0 ? $"{skipped.Count} more will be skipped because they need administrator rights or are locked. " : "";
        footer += MainWindow.IsAdmin
            ? "Tman creates a restore point first."
            : "Tman can't create a restore point without administrator rights, but you can still undo each tweak here.";
        if (!ConfirmList($"Apply {label}? {(run.Count == 1 ? "This change" : $"These {run.Count} changes")} will be made:", run, footer)) return;

        if (MainWindow.IsAdmin)
        {
            SetBusy(true);
            CountText.Text = "Creating a restore point…";
            var error = await Task.Run(() => Tweaks.CreateRestorePoint($"Before Tman: {label}"));
            SetBusy(false);
            UpdateCount();
            if (error != null && !Ui.Confirm($"Tman couldn't create a restore point:\n\n{error}\n\nContinue anyway? You can still undo each tweak here.", "Tweaks")) return;
        }
        await Run(run.Select(i => (i, true)).ToList());
    }

    async void Preset_Click(object sender, RoutedEventArgs e)
    {
        var preset = Enum.Parse<TweakPreset>((string)((Button)sender).Tag);
        var ids = Tweaks.InPreset(preset).ToHashSet();
        await ApplySet(_items.Where(i => ids.Contains(i.Tweak)), $"the {preset} preset");
    }

    async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        var changed = _items.Where(i => i.Changed).ToList();
        if (changed.Count == 0) return;
        if (!ConfirmList($"Put back the old settings for {(changed.Count == 1 ? "this tweak" : $"these {changed.Count} tweaks")}?", changed)) return;
        await Run(changed.Select(i => (i, false)).ToList());
    }

    bool ConfirmList(string question, IEnumerable<TweakItem> items, string? footer = null) =>
        new ListConfirmDialog("Tweaks", question, items.Select(i => i.Name), footer) { Owner = Window.GetWindow(this) }.ShowDialog() == true;

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "tman-tweaks.json", Filter = "Tman tweaks (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var ids = _items.Where(i => i.State == TweakState.On).Select(i => i.Tweak.Id).ToArray();
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { tweaks = ids }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't save the file:\n\n{ex.Message}");
        }
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Tman tweaks (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        HashSet<string> ids;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
            ids = doc.RootElement.GetProperty("tweaks").EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet();
        }
        catch (Exception ex)
        {
            Ui.Error($"That file isn't a Tman tweaks file:\n\n{ex.Message}");
            return;
        }
        await ApplySet(_items.Where(i => ids.Contains(i.Tweak.Id)), Path.GetFileName(dialog.FileName));
    }

    async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Restart Explorer? The taskbar disappears for a moment and open File Explorer windows close.", "Tweaks")) return;
        ExplorerButton.IsEnabled = false;
        await Task.Run(RestartExplorer);
        ExplorerButton.IsEnabled = true;
        _pending.Remove(TweakEffect.Explorer);
        UpdatePending();
    }

    internal static void RestartExplorer()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (p.SessionId == session) p.Kill();
            }
            catch { }
        }
        // Windows normally brings the shell back by itself.
        Thread.Sleep(3000);
        if (Process.GetProcessesByName("explorer").All(p => p.SessionId != session))
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }
}
