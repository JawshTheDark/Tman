using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public partial class PanelsPage : UserControl, IPage
{
    public sealed record Tool(string Name, string What, string File, string Args = "", string? Missing = null);

    const string ProOnly = "It only comes with Windows Pro, Enterprise and Education, so this edition of Windows doesn't have it.";

    static readonly (string Group, Tool[] Tools)[] Tools =
    {
        ("EVERYDAY SETTINGS", new Tool[]
        {
            new("Control Panel", "The classic settings window.", "control.exe"),
            new("Programs and Features", "Uninstall or change desktop programs.", "appwiz.cpl"),
            new("Network Connections", "Adapters and their IP settings.", "ncpa.cpl"),
            new("Power Options", "Power plans and what the power button does.", "powercfg.cpl"),
            new("Sound", "Playback and recording devices.", "mmsys.cpl"),
            new("Display", "Screen resolution and scaling.", "desk.cpl"),
            new("Mouse", "Buttons, pointer speed and pointers.", "main.cpl"),
            new("Fonts", "See, add and remove fonts.", "Fonts"),
            new("Region", "Date formats and the language for older programs.", "intl.cpl"),
            new("Date and Time", "Clock, time zone and internet time.", "timedate.cpl"),
            new("Character Map", "Find and copy special characters.", "charmap.exe"),
        }),
        ("SYSTEM", new Tool[]
        {
            new("System Properties", "Computer name, hardware and remote access.", "sysdm.cpl"),
            new("Advanced system settings", "Performance, virtual memory and startup options.", "SystemPropertiesAdvanced.exe"),
            new("User Accounts", "Manage users and set up automatic sign-in.", "netplwiz.exe"),
            new("System Configuration", "Boot options and startup troubleshooting.", "msconfig.exe"),
            new("Windows Tools", "The folder of all Windows admin tools.", "control.exe", "admintools"),
        }),
        ("HARDWARE AND DISKS", new Tool[]
        {
            new("Device Manager", "See hardware and manage drivers.", "devmgmt.msc"),
            new("Disk Management", "Partitions, volumes and drive letters.", "diskmgmt.msc"),
        }),
        ("ADMINISTRATION", new Tool[]
        {
            new("Computer Management", "Disks, users, services and logs in one window.", "compmgmt.msc"),
            new("Services", "Start, stop and configure background services.", "services.msc"),
            new("Task Scheduler", "See and create scheduled tasks.", "taskschd.msc"),
            new("Registry Editor", "Browse and edit Windows' settings database.", "regedit.exe"),
            new("Group Policy", "Local policy settings. Pro and above.", "gpedit.msc", Missing: ProOnly),
            new("Local Security Policy", "Password, audit and rights policies. Pro and above.", "secpol.msc", Missing: ProOnly),
        }),
        ("MONITORING AND DIAGNOSTICS", new Tool[]
        {
            new("Event Viewer", "Windows' logs of errors and events.", "eventvwr.msc"),
            new("Performance Monitor", "Graphs and logs of performance counters.", "perfmon.msc"),
            new("Resource Monitor", "Live CPU, memory, disk and network by process.", "resmon.exe"),
            new("Reliability Monitor", "A timeline of crashes and changes.", "perfmon.exe", "/rel"),
            new("DirectX Diagnostic", "Graphics and sound details for troubleshooting.", "dxdiag.exe"),
        }),
    };

    readonly List<UniformGrid> _grids = new();

    public PanelsPage()
    {
        InitializeComponent();
        foreach (var (group, tools) in Tools)
        {
            // Each tile carries 4 all round, which makes them 8 apart. The grid pulls the outer 4 back in.
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-4) };
            foreach (var tool in tools) grid.Children.Add(MakeButton(tool));
            _grids.Add(grid);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = group, Style = (Style)FindResource("PanelLabel"), Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(grid);
            // 12 under the page's subtitle, then the cards are 10 apart.
            Groups.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, _grids.Count == 1 ? 12 : 10, 0, 0), Child = panel });
        }
        Groups.SizeChanged += (_, e) =>
        {
            int columns = Math.Clamp((int)(e.NewSize.Width / 260), 1, 4);
            foreach (var g in _grids) g.Columns = columns;
        };
    }

    Button MakeButton(Tool tool)
    {
        var name = new TextBlock { Text = tool.Name, Style = (Style)FindResource("CardHeading"), TextWrapping = TextWrapping.Wrap };
        var what = new TextBlock { Text = tool.What, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), Style = (Style)FindResource("Subtle") };
        var label = new StackPanel();
        label.Children.Add(name);
        label.Children.Add(what);
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(4),
            Padding = new Thickness(14, 10, 14, 10),
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("CardStrokeBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        button.Click += (_, _) => Open(tool);
        return button;
    }

    /// <summary>The tool's file, in System32 or the Windows folder (or the Fonts folder itself); null if this PC doesn't have it.</summary>
    public static string? Find(Tool tool)
    {
        foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            var path = Path.Combine(dir, tool.File);
            if (File.Exists(path) || Directory.Exists(path)) return path;
        }
        return null;
    }

    static void Open(Tool tool)
    {
        if (Find(tool) is not { } path)
        {
            Ui.Error($"{tool.Name} isn't on this PC. {tool.Missing ?? "It may have been removed from this copy of Windows."}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path, tool.Args) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no to Windows' permission prompt.
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't open {tool.Name}:\n\n{ex.Message}");
        }
    }
}
