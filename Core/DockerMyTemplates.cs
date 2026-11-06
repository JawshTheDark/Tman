using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tman.Core;

/// <summary>
/// Your own templates, kept in one file in the same Portainer format as any other list, so that it can be read, copied or
/// shared as one. Settings in it are stored as you typed them, passwords included.
/// </summary>
public static class DockerMyTemplates
{
    public const string SourceName = "My templates";

    // Portainer ignores fields it doesn't know, so these sit beside its own without spoiling the file.
    internal const string OptionsKey = "tman_options", ComposeKey = "tman_compose";

    public static string FilePath => Path.Combine(DockerTemplates.DataFolder, "docker-my-templates.json");

    public static List<DockerTemplate> Load()
    {
        try
        {
            return File.Exists(FilePath) ? DockerTemplates.Parse(File.ReadAllText(FilePath), SourceName, mine: true) : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Your templates are kept apart from the lists, as one card each, because merging would hide them among the versions of an app.</summary>
    public static DockerApp ToApp(DockerTemplate t) => new()
    {
        Title = t.Title,
        Description = t.Description,
        Categories = new[] { SourceName },
        Logos = DockerTemplates.IconAddresses(t.Title, new List<DockerTemplate> { t }),
        Versions = new List<DockerTemplate> { t },
    };

    public static JsonObject ToEntry(ContainerPlan plan, string title, string description)
    {
        var entry = Common((int)DockerTemplateKind.Container, title, description);
        entry["image"] = plan.Image.Trim();
        entry["name"] = plan.Name.Trim();
        if (plan.Restart != ContainerPlan.RestartNever) entry["restart_policy"] = plan.Restart;
        if (plan.Network.Length > 0) entry["network"] = plan.Network;
        if (plan.Privileged) entry["privileged"] = true;
        if (plan.Hostname.Length > 0) entry["hostname"] = plan.Hostname;
        if (plan.Command.Length > 0) entry["command"] = plan.Command;
        if (plan.Options.Trim().Length > 0) entry[OptionsKey] = plan.Options.Trim();

        var ports = new JsonArray();
        foreach (var p in plan.Ports.Where(p => !p.IsBlank && p.Host.Trim().Length > 0))
            ports.Add($"{p.Host.Trim()}:{p.Target.Trim()}/{p.Protocol}");
        entry["ports"] = ports;

        var volumes = new JsonArray();
        foreach (var v in plan.Volumes.Where(v => !v.IsBlank))
        {
            var volume = new JsonObject { ["container"] = v.Target.Trim() };
            // An unnamed volume belongs to one container. A new copy gets its own, or two apps would write over each other.
            if (!v.Unnamed) volume["bind"] = v.Source.Trim();
            if (v.ReadOnly) volume["readonly"] = true;
            volumes.Add(volume);
        }
        entry["volumes"] = volumes;

        entry["env"] = Env(plan.Env);
        var labels = new JsonArray();
        foreach (var l in plan.Labels.Where(l => l.Name.Trim().Length > 0))
            labels.Add(new JsonObject { ["name"] = l.Name.Trim(), ["value"] = l.Value });
        entry["labels"] = labels;
        return entry;
    }

    public static JsonObject ToEntry(StackPlan plan, string title, string description)
    {
        var entry = Common((int)DockerTemplateKind.Compose, title, description);
        entry["name"] = plan.Project;
        entry[ComposeKey] = plan.Yaml;
        entry["env"] = plan.Deployed != null ? Env(ParseEnv(plan.EnvText)) : Env(plan.Env);
        return entry;
    }

    static JsonObject Common(int type, string title, string description) => new()
    {
        ["type"] = type,
        ["title"] = title,
        ["description"] = description,
        ["categories"] = new JsonArray(SourceName),
    };

    static JsonArray Env(IEnumerable<PlanEnv> env)
    {
        var list = new JsonArray();
        foreach (var e in env.Where(e => e.Name.Trim().Length > 0))
            list.Add(new JsonObject { ["name"] = e.Name.Trim(), ["label"] = e.Label.Length > 0 ? e.Label : e.Name.Trim(), ["default"] = e.Value });
        return list;
    }

    /// <summary>Reads NAME=value lines from a .env file, for saving a stack as a template.</summary>
    internal static List<PlanEnv> ParseEnv(string text)
    {
        var rows = new List<PlanEnv>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = line.IndexOf('=');
            if (line.StartsWith('#') || equals <= 0) continue;
            rows.Add(new PlanEnv { Name = line[..equals].Trim(), Value = line[(equals + 1)..].Trim().Trim('\'', '"'), Typed = true });
        }
        return rows;
    }

    /// <summary>Adds a template, or replaces yours with the same title. Throws if the file can't be written.</summary>
    public static void Save(JsonObject entry)
    {
        var title = entry["title"]!.GetValue<string>();
        var templates = ReadEntries();
        var same = templates.Where(t => string.Equals(t?["title"]?.GetValue<string>(), title, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var old in same) templates.Remove(old);
        templates.Add(entry);
        Write(templates);
    }

    public static bool Exists(string title) => Load().Any(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

    /// <summary>Throws if the file can't be written.</summary>
    public static void Remove(string title)
    {
        var templates = ReadEntries();
        foreach (var old in templates.Where(t => string.Equals(t?["title"]?.GetValue<string>(), title, StringComparison.OrdinalIgnoreCase)).ToList())
            templates.Remove(old);
        Write(templates);
    }

    static JsonArray ReadEntries()
    {
        if (!File.Exists(FilePath)) return new JsonArray();
        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath))?["templates"]?.AsArray() is { } list ? list : new JsonArray();
        }
        catch (JsonException)
        {
            // Hand-edited into something unreadable. Keep it, rather than saving over it.
            File.Move(FilePath, FilePath + ".bad", overwrite: true);
            return new JsonArray();
        }
    }

    static void Write(JsonArray templates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // A root that still belongs to its old parent can't be added to a new one.
        var file = new JsonObject { ["version"] = "3", ["templates"] = JsonNode.Parse(templates.ToJsonString()) };
        File.WriteAllText(FilePath, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
