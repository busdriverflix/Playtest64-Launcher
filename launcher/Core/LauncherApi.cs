using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Playtest64.Launcher;

/// <summary>
/// The three things the launcher asks of the website: a login, the list of builds and a file. They are
/// the website's own routes; the launcher is signed in with the same token a browser would get.
/// </summary>
public sealed class LauncherApi(HttpClient http, Settings settings)
{
    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        // "launcher" asks for a token that lasts long enough that nobody signs in every two weeks
        var body = LoginBody(email, password);

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Server + "/api/auth/login")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = TryParse(text);

        var token = json?.RootElement.TryGetProperty("token", out var t) == true ? t.GetString() : null;

        if (response.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(token))
        {
            // the server's own words are the best explanation (wrong password, locked for a while)
            var message = json?.RootElement.TryGetProperty("message", out var m) == true ? m.GetString() : null;
            throw new LauncherException(message ?? "Die Anmeldung ist fehlgeschlagen.");
        }

        settings.Token = token;
        settings.Email = email;
        settings.Save();
    }

    public async Task<IReadOnlyList<BuildInfo>> GetBuildsAsync(CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Get, "/api/builds");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        CheckSignedIn(response);

        using var json = TryParse(await response.Content.ReadAsStringAsync(ct));

        if (response.StatusCode != HttpStatusCode.OK || json?.RootElement.ValueKind != JsonValueKind.Array)
            throw new LauncherException("Die Liste der Builds konnte nicht geladen werden.");

        var builds = new List<BuildInfo>();

        foreach (var entry in json.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var file in files.EnumerateArray())
            {
                if (Text(file, "platform") != Platform.Name || !Flag(file, "available"))
                    continue;

                builds.Add(new BuildInfo(
                    (int)Number(entry, "id"), (int)Number(file, "id"), Text(entry, "version", "?"), Text(entry, "notes"),
                    Text(file, "fileName", "build.zip"), Number(file, "fileSize")));

                break;
            }
        }

        return builds;
    }

    public async Task<NewsFeed> GetNewsAsync(CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Get, "/api/news");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        CheckSignedIn(response);

        using var json = TryParse(await response.Content.ReadAsStringAsync(ct));

        if (response.StatusCode != HttpStatusCode.OK || json?.RootElement.TryGetProperty("articles", out var articles) != true ||
            articles.ValueKind != JsonValueKind.Array)
            throw new LauncherException("Die Neuigkeiten konnten nicht geladen werden.");

        var items = new List<NewsItem>();

        foreach (var article in articles.EnumerateArray())
        {
            var pictures = new List<NewsPicture>();

            if (article.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                foreach (var image in images.EnumerateArray())
                    pictures.Add(new NewsPicture((int)Number(image, "id"), Text(image, "caption")));
            }

            string? version = null;

            if (article.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object)
                version = Text(build, "version");

            items.Add(new NewsItem((int)Number(article, "id"), Text(article, "title"), Text(article, "content"),
                Text(article, "date"), pictures, version));
        }

        return new NewsFeed(items, Text(json.RootElement, "mediaTicket"));
    }

    /// <summary>The bytes of one picture of a post (the ticket is the one the news came with).</summary>
    public async Task<byte[]> GetPictureAsync(int id, string ticket, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.Server}/api/news/media/{id}?ticket={Uri.EscapeDataString(ticket)}");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        if (response.StatusCode != HttpStatusCode.OK)
            throw new LauncherException("Ein Bild konnte nicht geladen werden.");

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Downloads a build's file to `path`. `progress` gets 0..1 as it arrives (nothing when the size is not known).</summary>
    public async Task DownloadAsync(BuildInfo build, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var request = Authorised(HttpMethod.Get, $"/api/builds/files/{build.FileId}/download");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        CheckSignedIn(response);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var json = TryParse(await response.Content.ReadAsStringAsync(ct));
            var message = json?.RootElement.TryGetProperty("message", out var m) == true ? m.GetString() : null;
            throw new LauncherException(message ?? "Der Download ist fehlgeschlagen.");
        }

        var total = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long received = 0;
        int read;

        try
        {
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                if (total > 0)
                    progress?.Report((double)received / total);
            }
        }
        catch (IOException ex)
        {
            throw new LauncherException("Der Download wurde unterbrochen.", ex);
        }
    }

    // Written by hand: serializing an anonymous object looks at its type at run time, which a trimmed program cannot do
    private static string LoginBody(string email, string password)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("email", email);
            writer.WriteString("password", password);
            writer.WriteBoolean("launcher", true);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private HttpRequestMessage Authorised(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, settings.Server + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);

        return request;
    }

    // A server that cannot be reached is a sentence for the person, not an exception to crash on
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, option, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LauncherException("Keine Verbindung zum Server.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LauncherException("Der Server antwortet nicht.", ex);
        }
    }

    private void CheckSignedIn(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return;

        // the token is dead: keep it no longer, so that nothing keeps asking with it
        settings.SignOut();
        throw new SignedOutException();
    }

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name, string fallback = "")
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    // A number as the server wrote it: as a number, or as a text of digits (which it has been seen to do)
    private static long Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => 0,
        };
    }

    private static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
