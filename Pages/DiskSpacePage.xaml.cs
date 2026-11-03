using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public sealed class DirNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public DirNode? Parent { get; init; }
    public List<DirNode> Children { get; } = new();
    public long OwnFiles;
    public long OwnSize;
    public long Size;
    public long Files;
    public long Folders;
}

public sealed record DirRow(string Name, string Path, long Size, double Fraction, long Files, long Folders, DirNode? Node)
{
    public string Glyph => Node == null ? "" : "";
    public string SizeText => Fmt.Bytes(Size);
    public string PercentText => $"{Fraction * 100:0.0}%";
    public string FilesText => Fmt.Num(Files);
    public string FoldersText => Node == null ? "" : Fmt.Num(Folders);
}

public sealed record DriveCard(string Root, string Title, string Format, double Fraction, string Text, Brush Brush);

public partial class DiskSpacePage : UserControl, IPage
{
    static readonly EnumerationOptions Enumerate = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // don't follow junctions/symlinks (loops, double counting)
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    readonly DispatcherTimer _progress = new() { Interval = TimeSpan.FromMilliseconds(250) };
    CancellationTokenSource? _cts;
    DirNode? _current;
    long _scannedFiles, _scannedBytes;

    public DiskSpacePage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        _progress.Tick += (_, _) => ProgressText.Text = $"Scanning… {Fmt.Num(Interlocked.Read(ref _scannedFiles))} files · {Fmt.Bytes(Interlocked.Read(ref _scannedBytes))}";
    }

    public void OnShown() => LoadDrives();

    void LoadDrives()
    {
        var cards = new List<DriveCard>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                double used = d.TotalSize - d.TotalFreeSpace;
                double frac = d.TotalSize > 0 ? used / d.TotalSize : 0;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType == DriveType.Fixed ? "Local Disk" : d.DriveType.ToString() : d.VolumeLabel;
                var brush = (Brush)FindResource(frac > 0.9 ? "BadBrush" : "AccentBrush");
                cards.Add(new DriveCard(d.RootDirectory.FullName, $"{label} ({d.Name.TrimEnd('\\')})", $"{d.DriveFormat} · {d.DriveType}", frac,
                    $"{Fmt.Bytes(d.TotalFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}", brush));
            }
            catch { }
        }
        Drives.ItemsSource = cards;
    }

    void AnalyzeDrive_Click(object sender, RoutedEventArgs e) => Analyze((string)((Button)sender).Tag);

    void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to analyze" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) Analyze(dlg.FolderName);
    }

    internal async void Analyze(string root)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _scannedFiles = _scannedBytes = 0;
        _current = null;
        List.ItemsSource = null;
        UpButton.IsEnabled = false;
        PathText.Text = root;
        CancelButton.Visibility = Visibility.Visible;
        _progress.Start();
        var sw = Stopwatch.StartNew();
        try
        {
            var node = await Task.Run(() =>
            {
                var n = new DirNode { Name = root, Path = root };
                Scan(n, cts.Token, depth: 0);
                return n;
            });
            if (cts.IsCancellationRequested) return;
            ProgressText.Text = $"{Fmt.Num(node.Files)} files · {Fmt.Num(node.Folders)} folders · {Fmt.Bytes(node.Size)} · {sw.Elapsed.TotalSeconds:0.0}s";
            Show(node);
        }
        catch (OperationCanceledException)
        {
            if (cts == _cts) ProgressText.Text = "Cancelled";
        }
        finally
        {
            if (cts == _cts)
            {
                _progress.Stop();
                CancelButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    void Scan(DirNode node, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            foreach (var e in new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", Enumerate))
            {
                if (e is FileInfo f)
                {
                    node.OwnSize += f.Length;
                    node.OwnFiles++;
                }
                else
                {
                    node.Children.Add(new DirNode { Name = e.Name, Path = e.FullName, Parent = node });
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        Interlocked.Add(ref _scannedFiles, node.OwnFiles);
        Interlocked.Add(ref _scannedBytes, node.OwnSize);

        // Fan out near the root where the big subtrees are; deeper levels run inline.
        if (depth < 2 && node.Children.Count > 1)
            Parallel.ForEach(node.Children, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
                c => Scan(c, ct, depth + 1));
        else
            foreach (var c in node.Children) Scan(c, ct, depth + 1);

        node.Size = node.OwnSize;
        node.Files = node.OwnFiles;
        node.Folders = node.Children.Count;
        foreach (var c in node.Children)
        {
            node.Size += c.Size;
            node.Files += c.Files;
            node.Folders += c.Folders;
        }
    }

    void Show(DirNode node)
    {
        _current = node;
        PathText.Text = node.Path;
        UpButton.IsEnabled = node.Parent != null;
        double total = Math.Max(1, node.Size);
        var rows = node.Children
            .Select(c => new DirRow(c.Name, c.Path, c.Size, c.Size / total, c.Files, c.Folders, c))
            .ToList();
        if (node.OwnFiles > 0)
            rows.Add(new DirRow($"[{Fmt.Num(node.OwnFiles)} files in this folder]", node.Path, node.OwnSize, node.OwnSize / total, node.OwnFiles, 0, null));
        List.ItemsSource = rows.OrderByDescending(r => r.Size).ToList();
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Parent is { } p) Show(p);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Open_Click(sender, e);

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DirRow { Node: { } n } && n.Children.Count + n.OwnFiles > 0) Show(n);
    }

    void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DirRow r) Ui.OpenFileLocation(r.Path);
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DirRow r) Ui.Copy(r.Path);
    }
}
