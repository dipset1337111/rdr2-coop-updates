using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace CoopStory.Launcher;

public sealed record InternetRelease(int SchemaVersion, string BuildId, string DownloadUrl, string Sha256, long Size);

// Downloads are never extracted over the running launcher or the game directory.
public sealed class InternetUpdateService
{
    public const long MaxArchiveBytes = 300L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _client;
    private readonly string _repository;

    public InternetUpdateService(HttpClient client, string repository)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repository,
                @"\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z"))
            throw new LauncherException("Invalid update repository.");
        _client = client;
        _repository = repository;
    }

    public async Task<InternetRelease> CheckAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://github.com/{_repository}/releases/latest/download/update.json";
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(cancellationToken), buffer, 32768, cancellationToken);
        var release = JsonSerializer.Deserialize<InternetRelease>(buffer.ToArray(), Json)
            ?? throw new LauncherException("The update manifest is empty.");
        Validate(release);
        return release;
    }

    public void Validate(InternetRelease release)
    {
        var prefix = $"https://github.com/{_repository}/releases/download/";
        if (release.SchemaVersion != 1 || string.IsNullOrWhiteSpace(release.BuildId) || release.BuildId.Length > 100 ||
            release.Size <= 0 || release.Size > MaxArchiveBytes ||
            !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256 ?? "", @"\A[0-9a-fA-F]{64}\z") ||
            !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri) ||
            !release.DownloadUrl.StartsWith(prefix, StringComparison.Ordinal) ||
            uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith($"/{_repository}/releases/download/", StringComparison.Ordinal))
            throw new LauncherException("The update manifest has an invalid source, hash or size.");
    }

    public async Task<string> StageAsync(InternetRelease release, string bootstrapRoot,
        CancellationToken cancellationToken = default)
    {
        Validate(release);
        AssertNoLinks(bootstrapRoot);
        var updates = Path.Combine(bootstrapRoot, ".updates");
        Directory.CreateDirectory(updates);
        AssertNoLinks(updates);
        var stage = Path.Combine(updates, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var archivePath = Path.Combine(stage, "download.zip");
        using (var response = await _client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyLimitedAsync(await response.Content.ReadAsStreamAsync(cancellationToken), output, release.Size, cancellationToken);
        }
        if (new FileInfo(archivePath).Length != release.Size ||
            !Hashing.FileSha256(archivePath).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("Update download is incomplete or its checksum is incorrect. Current version is unchanged.");
        ExtractValidated(archivePath, stage);
        var package = Path.Combine(stage, "CoopStory");
        if (!PackageLocator.TryCreate(package, out _) || !File.Exists(Path.Combine(package, "CoopStory.Launcher.exe")))
            throw new LauncherException("The update does not contain a complete launcher package.");
        File.WriteAllText(Path.Combine(package, "UPDATE_BUILD_ID.txt"), release.BuildId);
        return package;
    }

    public static void ExtractValidated(string archivePath, string destination)
    {
        AssertNoLinks(destination);
        using var zip = ZipFile.OpenRead(archivePath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (zip.Entries.Count > 10000) throw new LauncherException("Too many update files.");
        // Validate every entry before writing any extracted files.
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (name.Length == 0 || name.StartsWith('/') ||
                parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                    p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    System.Text.RegularExpressions.Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ||
                !names.Add(name.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                entry.Length > MaxArchiveBytes || (total += entry.Length) > 1024L * 1024 * 1024)
                throw new LauncherException("The update archive contains an unsafe path or excessive content.");
            if (parts.Any(p => p.Equals("ScriptHookRDR2.dll", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("launcher-settings.json", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("install-manifest.json", StringComparison.OrdinalIgnoreCase)))
                throw new LauncherException("The update contains runtime or personal state files.");
            if (!name.StartsWith("CoopStory/", StringComparison.Ordinal) && name != "CoopStory/" &&
                name != "update-repository.txt" && name != "START_COOP.bat" && name != "Launch-Coop.ps1" && name != "SHA256SUMS.txt")
                throw new LauncherException("Unexpected update archive layout.");
        }
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.Replace('\\', '/').StartsWith("CoopStory/", StringComparison.Ordinal)) continue;
            var path = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AssertNoLinks(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    public static void AssertNoLinks(string path)
    {
        for (var cursor = new DirectoryInfo(Path.GetFullPath(path)); cursor != null; cursor = cursor.Parent)
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new LauncherException("Update folders cannot be symbolic links.");
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        await using (input)
        {
            var bytes = new byte[128 * 1024];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(bytes, token)) != 0)
            {
                total += count;
                if (total > limit) throw new LauncherException("Update download exceeded its permitted size.");
                await output.WriteAsync(bytes.AsMemory(0, count), token);
            }
        }
    }
}
