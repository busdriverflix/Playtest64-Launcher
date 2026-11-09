using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Playtest64.Launcher.Tests;

/// <summary>A stand-in for the playtest server: the three routes the launcher uses, with real zips to download.</summary>
public sealed class FakeServer : IDisposable
{
    public const string Token = "test-token";
    public const string Email = "tester@example.com";
    public const string Password = "correct horse";

    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public string Url { get; }
    public string? LastLoginBody { get; private set; }

    /// <summary>The zip of each file id. Replace one to serve something else (a damaged zip, a hostile one).</summary>
    public Dictionary<int, byte[]> Zips { get; } = new() { [1] = MakeZip("0.2.0"), [2] = MakeZip("0.2.1") };

    /// <summary>The launcher the server offers: a version (null = none uploaded), its bytes, and the hash it claims (null = the right one).</summary>
    public string? LauncherVersion { get; set; }
    public byte[] LauncherFile { get; set; } = Encoding.UTF8.GetBytes("#!/bin/sh\necho new launcher\n");
    public string? LauncherHash { get; set; }

    /// <summary>The news posts, newest first, as the real server writes them (a post without a build has null).</summary>
    public List<object> Articles { get; } =
    [
        new
        {
            id = 8, title = "Update: Animationen", content = "Neu:\n- Skid\n- Seitensalto\n\nViel Spaß!", date = "2026-10-30",
            createdAt = "2026-10-30T12:00:00Z", images = new[] { new { id = 3, caption = (string?)"Ein Bild" } },
            build = new { id = 2, version = "0.2.1" },
        },
        new { id = 6, title = "Ankündigung", content = "Hallo", date = "2026-10-25", createdAt = "2026-10-25T12:00:00Z", images = Array.Empty<object>(), build = (object?)null },
    ];

    /// <summary>The builds the list shows, newest first.</summary>
    public List<object> Builds { get; }

    public FakeServer()
    {
        var port = FreePort();

        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();

        Builds =
        [
            Build(2, "0.2.1", "Neuer Build", Zips[2].Length, linux: true, windows: true),
            Build(1, "0.2.0", "", Zips[1].Length, linux: true, windows: false),
        ];

        _loop = Task.Run(Serve);
    }

    public static byte[] MakeZip(string version, string? extraEntry = null)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var top = $"GameA-linux-x64-v{version}";

            var game = zip.CreateEntry($"{top}/game", CompressionLevel.Optimal);
            game.ExternalAttributes = 0b_100_000_111_101_101 << 16;			// 0100755
            using (var w = new StreamWriter(game.Open()))
                w.Write("#!/bin/sh\necho started > started.txt\necho \"graphics=${P64_GRAPHICS:-none}\" > graphics.txt\n");

            var pack = zip.CreateEntry($"{top}/game.p64pak", CompressionLevel.Optimal);
            using (var w = pack.Open())
                w.Write(Enumerable.Range(0, 256 * 2000).Select(i => (byte)i).ToArray());

            if (extraEntry != null)
            {
                var extra = zip.CreateEntry(extraEntry);
                using var w = new StreamWriter(extra.Open());
                w.Write("x");
            }
        }

        return stream.ToArray();
    }

    private static object Build(int id, string version, string notes, int size, bool linux, bool windows)
    {
        var files = new List<object>();

        if (windows)
            files.Add(new { id, platform = "Windows", fileName = $"GameA-windows-x64-v{version}.zip", fileSize = size, available = true });

        if (linux)
            files.Add(new { id, platform = "Linux", fileName = $"GameA-linux-x64-v{version}.zip", fileSize = size, available = true });

        return new { id, version, notes, uploadedAt = "2026-11-01T00:00:00Z", files };
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);

        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                Handle(context);
            }
            catch (Exception)
            {
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        var authorised = context.Request.Headers["Authorization"] == $"Bearer {Token}";

        if (context.Request.HttpMethod == "POST" && path == "/api/auth/login")
        {
            LastLoginBody = new StreamReader(context.Request.InputStream).ReadToEnd();
            using var json = JsonDocument.Parse(LastLoginBody);
            var root = json.RootElement;

            var good = root.GetProperty("email").GetString() == Email && root.GetProperty("password").GetString() == Password &&
                root.TryGetProperty("launcher", out var launcher) && launcher.GetBoolean();

            Json(context, good ? 200 : 401, good
                ? new { success = true, token = Token, email = Email, message = "Erfolgreich angemeldet." }
                : new { success = false, message = "E-Mail oder Passwort ist falsch." });
            return;
        }

        // a picture is asked for by an address that carries a ticket, like the website's <img> tags do
        if (path.StartsWith("/api/news/media/"))
        {
            if (!authorised && context.Request.QueryString["ticket"] != "ticket-1")
            {
                Json(context, 401, new { message = "Nicht angemeldet." });
                return;
            }

            context.Response.ContentType = "image/png";
            context.Response.OutputStream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            return;
        }

        if (!authorised)
        {
            Json(context, 401, new { message = "Nicht angemeldet." });
            return;
        }

        if (path == "/api/builds")
        {
            Json(context, 200, Builds);
            return;
        }

        if (path == "/api/news")
        {
            Json(context, 200, new { mediaTicket = "ticket-1", articles = Articles });
            return;
        }

        if (path == "/api/launcher/latest")
        {
            if (LauncherVersion == null)
            {
                Json(context, 404, new { message = "Für diese Plattform gibt es keinen Launcher." });
                return;
            }

            var hash = LauncherHash ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(LauncherFile)).ToLowerInvariant();

            Json(context, 200, new { id = 1, version = LauncherVersion, platform = context.Request.QueryString["platform"], fileName = "Playtest64Launcher", fileBytes = LauncherFile.Length, sha256 = hash, available = true });
            return;
        }

        if (path == "/api/launcher/files/1/download")
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = LauncherFile.Length;
            context.Response.OutputStream.Write(LauncherFile);
            return;
        }

        if (path.StartsWith("/api/builds/files/") && path.EndsWith("/download"))
        {
            var id = int.Parse(path.Split('/')[4]);

            if (!Zips.TryGetValue(id, out var data))
            {
                Json(context, 404, new { message = "Diese Datei existiert nicht." });
                return;
            }

            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = data.Length;
            context.Response.OutputStream.Write(data);
            return;
        }

        Json(context, 404, new { message = "no" });
    }

    private static void Json(HttpListenerContext context, int status, object body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
    }

    public void Dispose()
    {
        // the offline test stops the server itself and the rig stops it again afterwards
        if (_listener.IsListening)
            _listener.Stop();

        _listener.Close();
    }
}

/// <summary>A launcher pointed at a fake server, with a data folder of its own that goes away afterwards.</summary>
public sealed class Rig : IDisposable
{
    public FakeServer Server { get; } = new();
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "launcher-test-" + Guid.NewGuid().ToString("N"));
    public Settings Settings { get; }
    public LauncherApi Api { get; }
    public BuildStore Store { get; }

    public Rig(bool signedIn = true)
    {
        Directory.CreateDirectory(Dir);
        Settings = Settings.Load(Dir);
        Settings.Server = Server.Url;
        Api = new LauncherApi(new HttpClient(), Settings);
        Store = new BuildStore(Dir, Api);

        if (signedIn)
        {
            Settings.Token = FakeServer.Token;
            Settings.Save();
        }
    }

    public void Dispose()
    {
        Server.Dispose();

        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
