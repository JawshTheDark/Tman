using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public sealed record SecurityRow(SecurityItem Item)
{
    public string Section => Item.Section;
    public string StateText => Item.State.ToString();
    public Brush StateBrush => (Brush)Application.Current.FindResource(Item.State switch
    {
        SecurityState.Good => "GoodBrush",
        SecurityState.Attention => "WarnBrush",
        _ => "SubtleBrush",
    });
    public Visibility OpenVisibility => Item.Open != null ? Visibility.Visible : Visibility.Collapsed;
}

public partial class SecurityPage : UserControl, IPage
{
    static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(15);

    bool _loaded, _loading;

    public SecurityPage() => InitializeComponent();

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    async void Load()
    {
        if (_loading) return;
        _loading = true;
        Summary.Text = "Checking…";
        var checks = SecurityChecks.All.Select(c => Task.Run(() => c().ToList())).ToList();
        var items = new List<SecurityItem>();
        // A check that hangs drops out of the list and is counted, so the rest still show.
        var limit = Task.Delay(CheckLimit);
        int missed = 0;
        foreach (var check in checks)
        {
            if (await Task.WhenAny(check, limit) == check && check.IsCompletedSuccessfully) items.AddRange(check.Result);
            else missed++;
        }
        var rows = items.Select(i => new SecurityRow(i)).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SecurityRow.Section)));
        Rows.ItemsSource = view;
        var counts = new List<string>();
        foreach (var (state, word) in new[] { (SecurityState.Good, "good"), (SecurityState.Attention, "need attention"), (SecurityState.Off, "off"), (SecurityState.Unknown, "unknown") })
        {
            int n = items.Count(i => i.State == state);
            if (n > 0) counts.Add($"{n} {(n == 1 && state == SecurityState.Attention ? "needs attention" : word)}");
        }
        Summary.Text = string.Join(" · ", counts) + (missed > 0 ? $" · {missed} {(missed == 1 ? "check" : "checks")} didn't finish" : "");
        _loading = false;
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void OpenSecurity_Click(object sender, RoutedEventArgs e) => Open("windowsdefender:");

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string target) Open(target);
    }

    static void Open(string target)
    {
        try
        {
            var (file, args) = Ui.SplitCommand(target);
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't open that page: {ex.Message}");
        }
    }
}
