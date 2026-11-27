using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DiskSpace;

/// <summary>A GitHub release and the package asset that fits this install.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string Notes, string? AssetName, string? AssetUrl, long AssetSize);

/// <summary>How the running copy was installed, which decides how it can be updated.</summary>
public enum InstallKind
{
    /// <summary>Installed from an MSIX package: the new .msix is handed to App Installer.</summary>
    Msix,
    /// <summary>Single-file exe from the release zip: the exe is swapped in place and restarted.</summary>
    Zip,
    /// <summary>Development build (dll next to the exe): can't self-update, only the release page is offered.</summary>
    Development,
}

/// <summary>Checks GitHub for a newer release, downloads it and installs it.</summary>
public static class Updater
{
    public const string Repository = "stjeong/DiskSpace";
    public static readonly string ReleasesPage = $"https://github.com/{Repository}/releases";

    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    // Lazy: static initializers run in source order, and the client needs CurrentVersion (declared below).
    private static readonly Lazy<HttpClient> HttpLazy = new(CreateClient);
    private static HttpClient Http => HttpLazy.Value;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DiskSpace", CurrentVersion.ToString(3)));
        return http;
    }

    /// <summary>The running version (major.minor.build from the csproj &lt;Version&gt;).</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static InstallKind Kind { get; } =
        NativeMethods.IsPackaged() ? InstallKind.Msix
        : File.Exists(Path.Combine(AppContext.BaseDirectory, "DiskSpace.dll")) ? InstallKind.Development
        : InstallKind.Zip;

    /// <summary>"win-x64" / "win-arm64": the release asset suffix for this process.</summary>
    private static string RuntimeId => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64",
    };

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Reads the latest (non-prerelease) release from the GitHub API.</summary>
    public static async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("GitHub에 게시된 릴리스가 없습니다.");
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        JsonElement root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version))
            throw new InvalidOperationException($"릴리스 태그를 버전으로 읽을 수 없습니다: {tag}");

        string pageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        // Kind.Development has no matching asset; it only gets the release page.
        string? extension = Kind switch { InstallKind.Msix => ".msix", InstallKind.Zip => ".zip", _ => null };
        string? assetName = null, assetUrl = null;
        long assetSize = 0;
        if (extension is not null && root.TryGetProperty("assets", out JsonElement assets))
        {
            string wanted = $"-{RuntimeId}{extension}";
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    assetSize = asset.GetProperty("size").GetInt64();
                    break;
                }
            }
        }

        return new ReleaseInfo(Normalize(version), tag, pageUrl, notes.Trim(), assetName, assetUrl, assetSize);
    }

    /// <summary>Downloads the release asset into a temp folder, reporting progress as 0..1.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        if (release.AssetUrl is null || release.AssetName is null)
            throw new InvalidOperationException("이 설치 방식에 맞는 다운로드 파일이 릴리스에 없습니다.");

        string dir = Path.Combine(Path.GetTempPath(), "DiskSpace-update");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, release.AssetName);

        using HttpResponseMessage response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? release.AssetSize;

        try
        {
            await using Stream source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report((done, total));
            }
        }
        catch
        {
            TryDelete(file);
            throw;
        }

        return file;
    }

    /// <summary>
    /// Installs the downloaded package and starts the new version. Returns normally when the caller
    /// should exit the app; throws when nothing was changed.
    /// </summary>
    /// <param name="restartArgs">Command-line arguments for the restarted app (zip install only).</param>
    public static void Install(string packageFile, IEnumerable<string> restartArgs)
    {
        if (Kind == InstallKind.Msix)
        {
            // App Installer shows the update UI and can launch the app when it's done.
            Process.Start(new ProcessStartInfo(packageFile) { UseShellExecute = true });
            return;
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        string newExe = exe + NewSuffix;
        string oldExe = exe + OldSuffix;

        using (ZipArchive zip = ZipFile.OpenRead(packageFile))
        {
            ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("DiskSpace.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("다운로드한 zip 안에 DiskSpace.exe가 없습니다.");
            entry.ExtractToFile(newExe, overwrite: true); // fails early if the folder isn't writable
        }

        // A running exe can't be overwritten but can be renamed: move it aside, then put the new one in place.
        TryDelete(oldExe);
        File.Move(exe, oldExe);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(oldExe, exe); // roll back
            TryDelete(newExe);
            throw;
        }
        TryDelete(packageFile);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (string arg in restartArgs) psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    /// <summary>Removes the previous exe left behind by a zip update (it may still be exiting, so retry briefly).</summary>
    public static void CleanupAfterUpdate()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;
        string oldExe = exe + OldSuffix;
        if (!File.Exists(oldExe)) return;

        var thread = new Thread(() =>
        {
            for (int i = 0; i < 20 && File.Exists(oldExe); i++)
            {
                if (TryDelete(oldExe)) return;
                Thread.Sleep(500);
            }
        }) { IsBackground = true, Name = "Update cleanup" };
        thread.Start();
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
