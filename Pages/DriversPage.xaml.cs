using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Tman.Core;

namespace Tman.Pages;

public sealed record DriverEntry(string Device, string Class, string Provider, string Version, DateTime? Date, bool? Signed, string Inf)
{
    public string DateText => Date?.ToString("d") ?? "";
    public string SignedText => Signed switch { true => "Yes", false => "No", _ => "" };
}

public partial class DriversPage : UserControl, IPage
{
    List<DriverEntry> _all = new();
    ICollectionView? _view;
    string _filter = "";

    public DriversPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    async void Load()
    {
        CountText.Text = "Loading drivers (this takes a few seconds)…";
        _all = await Task.Run(() => Wmi.Query("SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, IsSigned, InfName FROM Win32_PnPSignedDriver")
            .Where(o => o.Str("DeviceName").Length > 0)
            .Select(o => new DriverEntry(o.Str("DeviceName"), Title(o.Str("DeviceClass")), o.Str("DriverProviderName"), o.Str("DriverVersion"),
                o.Date("DriverDate"), o.Bool("IsSigned"), o.Str("InfName")))
            .OrderBy(d => d.Device, StringComparer.OrdinalIgnoreCase)
            .ToList());
        List.ItemsSource = _all;
        _view = CollectionViewSource.GetDefaultView(_all);
        _view.Filter = Matches;
        ApplyFilter();
    }

    static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    bool Matches(object o)
    {
        var d = (DriverEntry)o;
        if (HideMicrosoft.IsChecked == true && d.Provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
        return _filter.Length == 0 || Ui.Contains(d.Device, _filter) || Ui.Contains(d.Class, _filter) || Ui.Contains(d.Provider, _filter) || Ui.Contains(d.Inf, _filter);
    }

    void ApplyFilter()
    {
        if (_view == null) return;
        _view.Refresh();
        var shown = _view.Cast<object>().Count();
        var unsigned = _all.Count(d => d.Signed == false);
        CountText.Text = (shown == _all.Count ? $"{_all.Count} drivers" : $"{shown} of {_all.Count} drivers") + (unsigned > 0 ? $" · {unsigned} unsigned" : "");
    }

    void HideMicrosoft_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); } catch { }
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DriverEntry d) Ui.SearchOnline($"{d.Device} driver {d.Version}");
    }

    void Copy_Click(object sender, RoutedEventArgs e) =>
        Ui.Copy(string.Join(Environment.NewLine, List.SelectedItems.Cast<DriverEntry>()
            .Select(d => $"{d.Device}\t{d.Class}\t{d.Provider}\t{d.Version}\t{d.DateText}\t{d.SignedText}\t{d.Inf}")));
}
