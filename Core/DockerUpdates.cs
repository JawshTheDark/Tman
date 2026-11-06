using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tman.Core;

public enum ImageUpdate { Unknown, Current, Newer, Pinned, Local }

/// <summary>
/// Asks a registry what an image's tag points at now, and compares that with what's here. Asking takes one small request
/// with no sign-in and downloads nothing. Docker Hub doesn't count this kind of request against its pull limit, which is
/// why this asks the registry directly rather than going through Docker.
/// </summary>
public static class DockerUpdates
{
    const string HubHost = "registry-1.docker.io";

    static readonly string[] ManifestTypes =
    {
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    };

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tman");
        return client;
    }

    public static string Describe(ImageUpdate update) => update switch
    {
        ImageUpdate.Newer => "Newer image",
        ImageUpdate.Current => "Up to date",
        ImageUpdate.Pinned => "Fixed version",
        ImageUpdate.Local => "Built here",
        _ => "Can't check",
    };

    /// <summary>Splits an image name into where it lives. Null when it's pinned to one exact build, so nothing can be newer.</summary>
    public static (string Host, string Repository, string Tag)? Parse(string image)
    {
        if (image.Contains('@') || image.StartsWith("sha256:")) return null;
        var name = image;
        var tag = "latest";
        int colon = name.LastIndexOf(':');
        if (colon > name.LastIndexOf('/'))
        {
            tag = name[(colon + 1)..];
            name = name[..colon];
        }
        var first = name.Split('/')[0];
        bool hasHost = name.Contains('/') && (first.Contains('.') || first.Contains(':') || first == "localhost");
        if (!hasHost) return (HubHost, name.Contains('/') ? name : "library/" + name, tag);
        var repository = name[(first.Length + 1)..];
        return (first is "docker.io" or "index.docker.io" ? HubHost : first, first is "docker.io" or "index.docker.io" && !repository.Contains('/') ? "library/" + repository : repository, tag);
    }

    /// <summary>Compares the registry's current answer with the digests the local image is known by.</summary>
    public static async Task<ImageUpdate> CheckAsync(string image, IReadOnlyCollection<string> repoDigests, CancellationToken cancel)
    {
        if (Parse(image) is not { } where) return ImageUpdate.Pinned;
        if (repoDigests.Count == 0) return ImageUpdate.Local;
        var digest = await CurrentDigestAsync(where.Host, where.Repository, where.Tag, cancel).ConfigureAwait(false);
        if (digest == null) return ImageUpdate.Unknown;
        return repoDigests.Any(d => d.EndsWith("@" + digest, StringComparison.OrdinalIgnoreCase)) ? ImageUpdate.Current : ImageUpdate.Newer;
    }

    /// <summary>The digest the registry gives for a tag, or null if it won't say, as with a private image.</summary>
    public static async Task<string?> CurrentDigestAsync(string host, string repository, string tag, CancellationToken cancel)
    {
        try
        {
            var url = $"https://{host}/v2/{repository}/manifests/{tag}";
            using var first = await AskAsync(url, null, cancel).ConfigureAwait(false);
            if (first.StatusCode != HttpStatusCode.Unauthorized) return Digest(first);
            var token = await TokenAsync(first, repository, cancel).ConfigureAwait(false);
            if (token == null) return null;
            using var second = await AskAsync(url, token, cancel).ConfigureAwait(false);
            return Digest(second);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return null;
        }
    }

    static Task<HttpResponseMessage> AskAsync(string url, string? token, CancellationToken cancel)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes) request.Headers.Accept.ParseAdd(type);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
    }

    static string? Digest(HttpResponseMessage response) =>
        response.IsSuccessStatusCode && response.Headers.TryGetValues("Docker-Content-Digest", out var values) ? values.FirstOrDefault() : null;

    /// <summary>Registries hand out an anonymous token for public images, and say where to get one when they refuse a request.</summary>
    static async Task<string?> TokenAsync(HttpResponseMessage refused, string repository, CancellationToken cancel)
    {
        var challenge = refused.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))?.Parameter;
        if (challenge == null) return null;
        string Part(string name) => Regex.Match(challenge, name + "=\"([^\"]*)\"").Groups[1].Value;
        var realm = Part("realm");
        if (!realm.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        var scope = Part("scope") is { Length: > 0 } given ? given : $"repository:{repository}:pull";
        var url = $"{realm}?service={Uri.EscapeDataString(Part("service"))}&scope={Uri.EscapeDataString(scope)}";
        using var response = await Http.GetAsync(url, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        return DockerJson.Text(doc.RootElement, "token") is { Length: > 0 } token ? token : DockerJson.Text(doc.RootElement, "access_token");
    }
}
