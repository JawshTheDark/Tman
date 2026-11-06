using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Path;
using Tman.Core;

namespace Tman.Controls;

/// <summary>
/// Docker's networks as a drawing you edit: this PC on the left, containers in the middle, networks on the right. Drag from
/// a dot to connect, select a line and press Delete to disconnect, and drag boxes wherever they read best. Edits go into a
/// <see cref="NetworkDraft"/> and appear as dashed lines. This control never talks to Docker.
/// </summary>
public sealed class DockerNetworkMap : Canvas
{
    const double ContainerWidth = 190, NetworkWidth = 170, PcWidth = 92, NodeHeight = 46, RowGap = 16;
    const double PcX = 12, ContainerX = 196, NetworkX = 476, TopY = 12, Edge = 12;
    const string PcKey = "pc";

    enum Kind { Pc, Container, Network }

    sealed class Node
    {
        public required string Key { get; init; }
        public required Kind Kind { get; init; }
        public required string Name { get; init; }
        public required Border Box { get; init; }
        public Ellipse? Dot { get; set; }
        public MapContainer? Container { get; init; }
        public MapNetwork? Network { get; init; }
        public bool Draft { get; init; }
        public bool Going { get; init; }
        public Point Left => new(GetLeft(Box), GetTop(Box) + NodeHeight / 2);
        public Point Right => new(GetLeft(Box) + Box.Width, GetTop(Box) + NodeHeight / 2);
    }

    sealed record Link(Node Container, Node Network, Line Stroke, Line Hit, bool Added, bool Going, bool Locked);

    sealed record PortLine(Node Container, Line Stroke, Border Label);

    readonly Dictionary<string, Node> _nodes = new();
    readonly List<Link> _links = new();
    readonly List<PortLine> _ports = new();
    readonly Dictionary<string, Point> _placed = LoadLayout();
    readonly Line _rubber = new() { StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };

    DockerMap _map = DockerMap.Empty;
    NetworkDraft _draft = new();
    string _drawn = "";
    Link? _selectedLink;
    Node? _selected, _moving, _linkFrom, _dropOn;
    Point _grabbedAt;

    /// <summary>A change to the draft, so the page can show the commands.</summary>
    public event Action? DraftChanged;
    /// <summary>The selection moved, or something needs saying. The text says what can be done, or why that couldn't be.</summary>
    public event Action<string>? Hint;
    public event Action<string>? ContainerOpened;

    public DockerNetworkMap()
    {
        Focusable = true;
        Background = Brushes.Transparent; // hit-testable everywhere, so clicking empty space clears the selection
        AutomationProperties.SetName(this, "Network map");
        MouseLeftButtonDown += (_, _) =>
        {
            Select(null, null);
            Focus();
        };
        MouseMove += OnMove;
        MouseLeftButtonUp += OnRelease;
        LostMouseCapture += (_, _) => EndDrag();
        KeyDown += OnKey;
    }

    public bool IsDragging => _moving != null || _linkFrom != null;

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    public string? SelectedNetwork => _selected?.Network?.Name;

    protected override Size MeasureOverride(Size available)
    {
        base.MeasureOverride(available);
        double right = 0, bottom = 0;
        foreach (var n in _nodes.Values)
        {
            right = Math.Max(right, GetLeft(n.Box) + n.Box.Width);
            bottom = Math.Max(bottom, GetTop(n.Box) + NodeHeight);
        }
        return new Size(right + Edge, bottom + Edge);
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>Draws the map, leaving it alone if nothing on it changed and while something is being dragged.</summary>
    public void Show(DockerMap map, NetworkDraft draft)
    {
        _map = map;
        _draft = draft;
        var drawn = map.Signature + "@" + draft.Signature;
        if (IsDragging || drawn == _drawn) return;
        _drawn = drawn;
        Rebuild();
    }

    void Rebuild()
    {
        var selectedKey = _selected?.Key;
        var selectedLink = _selectedLink == null ? default : (_selectedLink.Container.Name, _selectedLink.Network.Name);
        Children.Clear();
        _nodes.Clear();
        _links.Clear();
        _ports.Clear();
        _selected = null;
        _selectedLink = null;

        var networks = new List<(MapNetwork Network, bool Draft)>();
        networks.AddRange(_map.Networks.Select(n => (n, false)));
        networks.AddRange(_draft.Created.Select(n => (new MapNetwork(n.Name, "bridge", n.Subnet, n.Internal, ""), true)));

        var pc = AddNode(PcKey, Kind.Pc, "This PC", PcWidth, null, null, false, false);
        Place(pc, new Point(PcX, TopY));
        double y = TopY;
        foreach (var c in ContainersInOrder(networks.Select(n => n.Network.Name).ToList()))
        {
            var node = AddNode("c/" + c.Name, Kind.Container, c.Name, ContainerWidth, c, null, false, false);
            Place(node, _placed.GetValueOrDefault(node.Key, new Point(ContainerX, y)));
            y += NodeHeight + RowGap;
        }
        y = TopY;
        foreach (var (network, draft) in networks)
        {
            var node = AddNode("n/" + network.Name, Kind.Network, network.Name, NetworkWidth, null, network, draft, _draft.Removed.Contains(network.Name));
            Place(node, _placed.GetValueOrDefault(node.Key, new Point(NetworkX, y)));
            y += NodeHeight + RowGap;
        }

        foreach (var c in _map.Containers)
        {
            var from = _nodes["c/" + c.Name];
            foreach (var name in c.Networks)
            {
                if (_nodes.TryGetValue("n/" + name, out var to))
                    AddLink(from, to, added: false, going: _draft.Disconnecting.Contains((c.Name, name)), locked: c.Lock != null || to.Network!.Fixed);
            }
            if (c.Ports.Count > 0) AddPorts(pc, from, c);
        }
        foreach (var (container, network) in _draft.Connecting)
        {
            if (_nodes.TryGetValue("c/" + container, out var from) && _nodes.TryGetValue("n/" + network, out var to))
                AddLink(from, to, added: true, going: false, locked: false);
        }

        // Lines sit under the boxes, so a box being dragged passes over them.
        foreach (var link in _links)
        {
            Children.Add(link.Stroke);
            Children.Add(link.Hit);
        }
        foreach (var port in _ports) Children.Add(port.Stroke);
        foreach (var port in _ports) Children.Add(port.Label);
        foreach (var node in _nodes.Values)
        {
            Children.Add(node.Box);
            if (node.Dot != null) Children.Add(node.Dot);
        }
        Children.Add(_rubber);
        _rubber.Visibility = Visibility.Collapsed;
        Route();
        InvalidateMeasure();

        if (selectedKey != null && _nodes.TryGetValue(selectedKey, out var again)) Select(again, null, quiet: true);
        else if (_links.FirstOrDefault(l => (l.Container.Name, l.Network.Name) == selectedLink) is { } link) Select(null, link, quiet: true);
    }

    /// <summary>Containers next to the first network they share, so that lines cross as little as a simple order allows.</summary>
    IEnumerable<MapContainer> ContainersInOrder(List<string> networks)
    {
        int Rank(MapContainer c) => c.Networks.Select(n => networks.IndexOf(n)).Where(i => i >= 0).DefaultIfEmpty(networks.Count).Min();
        return _map.Containers.OrderBy(Rank).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    Node AddNode(string key, Kind kind, string name, double width, MapContainer? container, MapNetwork? network, bool draft, bool going)
    {
        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xF8)), TextTrimming = TextTrimming.CharacterEllipsis };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(title);
        var detail = Detail(container, network, draft);
        if (detail.Length > 0) stack.Children.Add(new TextBlock { Text = detail, FontSize = 10, Foreground = Res("SubtleBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });

        UIElement content = stack;
        if (container != null)
        {
            var row = new DockPanel();
            var led = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Fill = container.Running ? Res("GoodBrush") : Res("SubtleBrush") };
            row.Children.Add(led);
            row.Children.Add(stack);
            content = row;
        }

        var box = new Border
        {
            Width = width,
            Height = NodeHeight,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 4, 9, 4),
            Child = content,
            Cursor = kind == Kind.Pc ? Cursors.Arrow : Cursors.SizeAll,
            Focusable = false,
            ToolTip = Tip(name, container, network),
        };
        AutomationProperties.SetName(box, $"{(kind == Kind.Network ? "Network" : kind == Kind.Container ? "Container" : "")} {name}".Trim());

        var node = new Node { Key = key, Kind = kind, Name = name, Box = box, Container = container, Network = network, Draft = draft, Going = going };
        Paint(node);
        box.MouseLeftButtonDown += (_, e) => OnNodePress(node, e);
        box.MouseRightButtonUp += (_, e) =>
        {
            Select(node, null);
            e.Handled = true;
            if (MenuFor(node) is { } menu)
            {
                menu.PlacementTarget = box;
                menu.IsOpen = true;
            }
        };
        if (kind != Kind.Pc)
        {
            node.Dot = new Ellipse { Width = 12, Height = 12, Fill = Res("AccentBrush"), Stroke = Res("WindowBrush"), StrokeThickness = 2, Cursor = Cursors.Cross, ToolTip = "Drag this to connect" };
            node.Dot.MouseLeftButtonDown += (_, e) => BeginLink(node, e);
        }
        _nodes[key] = node;
        return node;
    }

    static string Detail(MapContainer? c, MapNetwork? n, bool draft)
    {
        if (c != null) return c.Image;
        if (n == null) return "";
        var parts = new List<string>();
        if (draft) parts.Add("new");
        if (n.Subnet.Length > 0) parts.Add(n.Subnet);
        else if (!draft) parts.Add(n.Driver);
        if (n.Internal) parts.Add("no internet");
        return string.Join(" · ", parts);
    }

    static string Tip(string name, MapContainer? c, MapNetwork? n)
    {
        if (c != null) return c.Stack.Length > 0 ? $"{name}\nPart of the stack {c.Stack}\nDouble-click for its settings" : $"{name}\nDouble-click for its settings";
        if (n == null) return "Published ports. Yellow ones can be reached from other devices.";
        return n.Stack.Length > 0 ? $"{name}\nPart of the stack {n.Stack}" : n.Name == "bridge" ? "Docker's default network" : name;
    }

    void Paint(Node node)
    {
        bool chosen = node == _selected || node == _dropOn;
        node.Box.Background = Res(node.Going ? "WindowBrush" : "CardBrush");
        node.Box.BorderBrush = node == _dropOn ? Res("GoodBrush") : chosen ? Res("AccentBrush") : node.Going ? Res("BadBrush") : node.Draft ? Res("GoodBrush") : Res("CardStrokeBrush");
        node.Box.BorderThickness = new Thickness(chosen || node.Draft || node.Going ? 2 : 1);
        node.Box.Opacity = node.Going ? 0.7 : 1;
    }

    void Place(Node node, Point at)
    {
        SetLeft(node.Box, Math.Max(Edge, at.X));
        SetTop(node.Box, Math.Max(Edge, at.Y));
        if (node.Dot == null) return;
        var side = node.Kind == Kind.Container ? node.Right : node.Left;
        SetLeft(node.Dot, side.X - node.Dot.Width / 2);
        SetTop(node.Dot, side.Y - node.Dot.Height / 2);
    }

    void AddLink(Node container, Node network, bool added, bool going, bool locked)
    {
        var line = new Line { StrokeThickness = 2, IsHitTestVisible = false };
        var hit = new Line { Stroke = Brushes.Transparent, StrokeThickness = 12, Cursor = locked ? Cursors.Arrow : Cursors.Hand };
        AutomationProperties.SetName(hit, $"{container.Name} to {network.Name}");
        var link = new Link(container, network, line, hit, added, going, locked);
        hit.MouseLeftButtonDown += (_, e) =>
        {
            Select(null, link);
            Focus();
            e.Handled = true;
        };
        hit.MouseRightButtonUp += (_, e) =>
        {
            Select(null, link);
            e.Handled = true;
            if (locked) return;
            var menu = new ContextMenu();
            menu.Items.Add(Item(going ? "Keep this connection" : added ? "Don't connect" : "Disconnect", () => RemoveLink(link)));
            menu.PlacementTarget = hit;
            menu.IsOpen = true;
        };
        PaintLink(link);
        _links.Add(link);
    }

    void PaintLink(Link link)
    {
        bool chosen = link == _selectedLink;
        link.Stroke.Stroke = chosen ? Res("AccentBrush") : link.Going ? Res("BadBrush") : link.Added ? Res("GoodBrush") : link.Locked ? Res("CardStrokeBrush") : Res("LabelBrush");
        link.Stroke.StrokeThickness = chosen ? 3 : 2;
        link.Stroke.StrokeDashArray = link.Added || link.Going ? new DoubleCollection { 4, 3 } : null;
    }

    void AddPorts(Node pc, Node container, MapContainer c)
    {
        bool open = c.Ports.Any(p => p.Open);
        var line = new Line { Stroke = open ? Res("WarnBrush") : Res("CardStrokeBrush"), StrokeThickness = 1.5, IsHitTestVisible = false };
        var shown = c.Ports[0].Text + (c.Ports.Count > 1 ? $" +{c.Ports.Count - 1}" : "");
        var label = new Border
        {
            Background = Res("WindowBrush"),
            Padding = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = true,
            ToolTip = (open ? "Other devices on your network can reach this.\n" : "Only this PC can reach this.\n") + string.Join("\n", c.Ports.Select(p => p.Text)),
            Child = new TextBlock { Text = shown, FontSize = 10, Foreground = open ? Res("WarnBrush") : Res("SubtleBrush") },
        };
        _ports.Add(new PortLine(container, line, label));
    }

    static Geometry Curve(Point from, Point to)
    {
        double pull = Math.Max(40, Math.Abs(to.X - from.X) / 2);
        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment(new Point(from.X + pull, from.Y), new Point(to.X - pull, to.Y), to, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    void Route()
    {
        foreach (var link in _links)
        {
            var curve = Curve(link.Container.Right, link.Network.Left);
            link.Stroke.Data = curve;
            link.Hit.Data = curve;
        }
        if (!_nodes.TryGetValue(PcKey, out var pc)) return;
        foreach (var port in _ports)
        {
            var end = new Point(GetLeft(port.Container.Box), GetTop(port.Container.Box) + NodeHeight / 2);
            port.Stroke.Data = Curve(pc.Right, end);
            port.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = port.Label.DesiredSize.Width;
            SetLeft(port.Label, Math.Max(pc.Right.X + 4, end.X - width - 4));
            SetTop(port.Label, end.Y - 16);
        }
    }

    // ---- Selecting ----

    void Select(Node? node, Link? link, bool quiet = false)
    {
        var oldNode = _selected;
        var oldLink = _selectedLink;
        _selected = node;
        _selectedLink = link;
        if (oldNode != null) Paint(oldNode);
        if (node != null) Paint(node);
        if (oldLink != null) PaintLink(oldLink);
        if (link != null) PaintLink(link);
        if (quiet) return;
        Hint?.Invoke(link != null ? LinkHint(link) : node?.Kind == Kind.Network ? NetworkHint(node) : node?.Kind == Kind.Container ? ContainerHint(node) : "");
    }

    static string LinkHint(Link link)
    {
        if (link.Locked) return link.Network.Network!.Fixed ? $"{link.Container.Name} is fixed to {link.Network.Name}. Change that in its settings." : link.Container.Container!.Lock ?? "";
        return link.Going ? "Press Delete to keep this connection." : link.Added ? "Press Delete to forget this connection." : $"Press Delete to disconnect {link.Container.Name} from {link.Network.Name}.";
    }

    static string NetworkHint(Node node)
    {
        var n = node.Network!;
        if (n.BuiltIn) return $"{n.Name} is one of Docker's own networks, and can't be removed.";
        return node.Going ? "Press Delete to keep this network." : "Press Delete to remove this network.";
    }

    static string ContainerHint(Node node) =>
        node.Container!.Lock ?? "Drag its dot to a network to connect it. Double-click for its settings.";

    // ---- Dragging ----

    void OnNodePress(Node node, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Select(node, null);
        Focus();
        if (e.ClickCount == 2)
        {
            if (node.Container != null) ContainerOpened?.Invoke(node.Name);
            return;
        }
        if (node.Kind == Kind.Pc) return;
        _moving = node;
        _grabbedAt = e.GetPosition(node.Box);
        CaptureMouse();
    }

    void BeginLink(Node node, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Focus();
        if (node.Container?.Lock is { } reason)
        {
            Hint?.Invoke(reason);
            return;
        }
        if (node.Network?.Fixed == true)
        {
            Hint?.Invoke($"Containers go on {node.Name} through their settings, not with a line.");
            return;
        }
        _linkFrom = node;
        _rubber.Stroke = Res("GoodBrush");
        _rubber.Visibility = Visibility.Visible;
        DrawRubber(e.GetPosition(this));
        CaptureMouse();
    }

    void DrawRubber(Point to)
    {
        if (_linkFrom == null) return;
        _rubber.Data = _linkFrom.Kind == Kind.Container ? Curve(_linkFrom.Right, to) : Curve(to, _linkFrom.Left);
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (_moving != null)
        {
            Place(_moving, new Point(at.X - _grabbedAt.X, at.Y - _grabbedAt.Y));
            Route();
            InvalidateMeasure();
            return;
        }
        if (_linkFrom == null) return;
        DrawRubber(at);
        var over = NodeAt(at);
        var target = over != null && CanLink(_linkFrom, over) == null ? over : null;
        if (target == _dropOn) return;
        var old = _dropOn;
        _dropOn = target;
        if (old != null) Paint(old);
        if (target != null) Paint(target);
    }

    Node? NodeAt(Point at) => _nodes.Values.FirstOrDefault(n =>
        at.X >= GetLeft(n.Box) && at.X <= GetLeft(n.Box) + n.Box.Width && at.Y >= GetTop(n.Box) && at.Y <= GetTop(n.Box) + NodeHeight);

    /// <summary>Why a line can't be drawn between these two, or null if it can.</summary>
    string? CanLink(Node from, Node to)
    {
        var container = from.Kind == Kind.Container ? from : to.Kind == Kind.Container ? to : null;
        var network = from.Kind == Kind.Network ? from : to.Kind == Kind.Network ? to : null;
        if (container == null || network == null) return "Lines go between a container and a network.";
        if (container.Container!.Lock is { } reason) return reason;
        if (network.Network!.Fixed) return $"Containers go on {network.Name} through their settings, not with a line.";
        if (network.Going) return $"{network.Name} is about to be removed.";
        bool joined = container.Container.Networks.Contains(network.Name) && !_draft.Disconnecting.Contains((container.Name, network.Name));
        return joined || _draft.Connecting.Contains((container.Name, network.Name)) ? $"{container.Name} is already on {network.Name}." : null;
    }

    void OnRelease(object sender, MouseButtonEventArgs e)
    {
        var at = e.GetPosition(this);
        if (_moving is { } moved)
        {
            _placed[moved.Key] = new Point(GetLeft(moved.Box), GetTop(moved.Box));
            SaveLayout();
        }
        if (_linkFrom is { } from)
        {
            if (NodeAt(at) is { } over)
            {
                if (CanLink(from, over) is { } reason) Hint?.Invoke(reason);
                else Connect((from.Kind == Kind.Container ? from : over).Name, (from.Kind == Kind.Network ? from : over).Name);
            }
        }
        EndDrag();
    }

    void EndDrag()
    {
        if (!IsDragging) return;
        _moving = null;
        _linkFrom = null;
        var old = _dropOn;
        _dropOn = null;
        if (old != null) Paint(old);
        _rubber.Visibility = Visibility.Collapsed;
        if (IsMouseCaptured) ReleaseMouseCapture();
        // Docker may have changed while the mouse was down.
        Show(_map, _draft);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsDragging)
        {
            EndDrag();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    // ---- Editing ----

    void Changed()
    {
        _drawn = "";
        Show(_map, _draft);
        DraftChanged?.Invoke();
    }

    /// <summary>Connects a container to a network in the draft. This is what dropping a line does.</summary>
    public void Connect(string container, string network)
    {
        _draft.Connect(container, network);
        Hint?.Invoke($"{container} will join {network} when you apply.");
        Changed();
    }

    public void DeleteSelected()
    {
        if (_selectedLink is { } link) RemoveLink(link);
        else if (_selected?.Network is { } network) RemoveNetwork(_selected, network);
    }

    void RemoveLink(Link link)
    {
        if (link.Locked)
        {
            Hint?.Invoke(LinkHint(link));
            return;
        }
        if (link.Added) _draft.Disconnect(link.Container.Name, link.Network.Name);
        else if (link.Going) _draft.Connect(link.Container.Name, link.Network.Name);
        else _draft.Disconnect(link.Container.Name, link.Network.Name);
        _selectedLink = null;
        Changed();
    }

    void RemoveNetwork(Node node, MapNetwork network)
    {
        if (network.BuiltIn)
        {
            Hint?.Invoke(NetworkHint(node));
            return;
        }
        if (node.Draft) _draft.Forget(network.Name);
        else if (node.Going) _draft.Restore(network.Name);
        else _draft.Remove(network, _map.Containers);
        _selected = null;
        Changed();
    }

    ContextMenu? MenuFor(Node node)
    {
        var menu = new ContextMenu();
        if (node.Container is { } c)
        {
            menu.Items.Add(Item("Settings…", () => ContainerOpened?.Invoke(c.Name)));
            var join = new MenuItem { Header = "Connect to" };
            foreach (var other in _nodes.Values.Where(n => n.Kind == Kind.Network && CanLink(node, n) == null).OrderBy(n => n.Name))
                join.Items.Add(Item(other.Name, () => Connect(c.Name, other.Name)));
            join.IsEnabled = join.Items.Count > 0;
            menu.Items.Add(join);
        }
        else if (node.Network is { } n)
        {
            var remove = Item(node.Going ? "Keep this network" : "Remove network", () => RemoveNetwork(node, n));
            remove.IsEnabled = !n.BuiltIn;
            menu.Items.Add(remove);
        }
        else
        {
            return null;
        }
        return menu;
    }

    static MenuItem Item(string text, Action clicked)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => clicked();
        return item;
    }

    /// <summary>Forgets every box you've moved, and lays the map out again.</summary>
    public void Tidy()
    {
        _placed.Clear();
        SaveLayout();
        _drawn = "";
        Rebuild();
        InvalidateMeasure();
    }

    // ---- Where the boxes go ----

    static string LayoutPath => Path.Combine(DockerTemplates.DataFolder, "docker-map.json");

    static Dictionary<string, Point> LoadLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return new();
            var saved = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(LayoutPath));
            return saved?.Where(p => p.Value.Length == 2).ToDictionary(p => p.Key, p => new Point(p.Value[0], p.Value[1])) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(DockerTemplates.DataFolder);
            File.WriteAllText(LayoutPath, JsonSerializer.Serialize(_placed.ToDictionary(p => p.Key, p => new[] { p.Value.X, p.Value.Y })));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
