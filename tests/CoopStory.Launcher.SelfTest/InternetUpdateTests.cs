using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CoopStory.Launcher;

internal static class InternetUpdateTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "coop-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = MakeZip("CoopStory/CoopStory.Launcher.exe", "CoopStory/CoopStoryBridge.asi",
                "CoopStory/config/coopstory.example.json", "CoopStory/sidecar/CoopStory.Sidecar.exe");
            var release = new InternetRelease(1, "test-1", "https://github.com/test/updates/releases/download/v1/build.zip",
                Hashing.BytesSha256(archive), archive.Length);
            using var http = new HttpClient(new Handler(archive, release));
            var service = new InternetUpdateService(http, "test/updates");
            var found = service.CheckAsync().GetAwaiter().GetResult();
            if (found != release) throw new Exception("Manifest roundtrip failed.");
            var staged = service.StageAsync(found, root).GetAwaiter().GetResult();
            if (!PackageLocator.TryCreate(staged, out _) || File.Exists(Path.Combine(root, "current-package.txt")))
                throw new Exception("Staging must produce a package without activating it.");
            Reject(() => service.Validate(release with { DownloadUrl = "http://github.com/test/updates/releases/download/v1/build.zip" }));
            Reject(() => service.Validate(release with { DownloadUrl = "https://github.com/other/repo/releases/download/v1/build.zip" }));
            Reject(() => service.Validate(release with { DownloadUrl = "https://github.com/test/updates/releases/download/../../../../other/file.zip" }));
            Reject(() => service.Validate(release with { Size = InternetUpdateService.MaxArchiveBytes + 1 }));
            Reject(() => service.StageAsync(release with { Sha256 = new string('0', 64) }, root).GetAwaiter().GetResult());
            Reject(() => service.StageAsync(release with { Size = archive.Length - 1 }, root).GetAwaiter().GetResult());
            foreach (var names in new[] {
                new[] { "CoopStory/../../escaped.txt" }, new[] { "CoopStory/x:stream" },
                new[] { "CoopStory/file", "CoopStory/FILE" }, new[] { "CoopStory/CON.txt" },
                new[] { "CoopStory/trailing. " }, new[] { "CoopStory/ScriptHookRDR2.dll" },
                new[] { "/absolute.txt" }, new[] { "CoopStory/launcher-settings.json" } })
            {
                var unsafeZip = Path.Combine(root, Guid.NewGuid() + ".zip");
                File.WriteAllBytes(unsafeZip, MakeZip(names));
                var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(destination);
                Reject(() => InternetUpdateService.ExtractValidated(unsafeZip, destination));
                if (Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length != 0)
                    throw new Exception("Rejected archive wrote files.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (LauncherException) { return; }
        throw new Exception("Unsafe or damaged update accepted.");
    }

    private static byte[] MakeZip(params string[] names)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in names) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("test"); }
        return buffer.ToArray();
    }

    private sealed class Handler(byte[] archive, InternetRelease release) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("update.json")
                    ? Encoding.UTF8.GetBytes(JsonSerializer.Serialize(release)) : archive)
            });
    }
}
