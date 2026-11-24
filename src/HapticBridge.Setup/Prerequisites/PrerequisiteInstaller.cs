using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HapticBridge.Audio.Voicemeeter;
using HapticBridge.Integrations.SoundSync;

namespace HapticBridge.Setup.Prerequisites;

public sealed class PrerequisiteInstaller
{
    private const long MaximumPackageBytes = 320 * 1024 * 1024;
    private static readonly HttpClient Downloads = new();
    private readonly Func<PrerequisiteId, (PrerequisiteState State, string? Detail)> _detect;
    private readonly Func<PrerequisitePackage, Func<bool>, IProgress<string>, CancellationToken, Task<int?>> _install;
    private readonly HashSet<PrerequisiteId> _pendingRestart = new();
    private readonly bool _supportsAutomaticInstall;

    public PrerequisiteInstaller(VoicemeeterManager voicemeeter)
        : this(id => PrerequisiteCatalog.Detect(id, voicemeeter), DownloadAndInstallAsync,
            RuntimeInformation.OSArchitecture == Architecture.X64)
    {
    }

    internal PrerequisiteInstaller(
        Func<PrerequisiteId, (PrerequisiteState, string?)> detect,
        Func<PrerequisitePackage, Func<bool>, IProgress<string>, CancellationToken, Task<int?>> install,
        bool supportsAutomaticInstall = true)
    {
        _detect = detect;
        _install = install;
        _supportsAutomaticInstall = supportsAutomaticInstall;
    }

    public IReadOnlyList<PrerequisiteItem> Inspect(SetupRoute route) =>
        PrerequisiteCatalog.For(route).Select(Inspect).ToArray();

    public async Task InstallAsync(SetupRoute route, IReadOnlyCollection<string> approvedNames,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        foreach (var package in PrerequisiteCatalog.For(route).Where(item => approvedNames.Contains(item.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool IsMissing() => Inspect(package).State == PrerequisiteState.Missing;
            if (!IsMissing()) continue;

            int? result;
            try
            {
                result = await _install(package, IsMissing, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException($"Windows canceled the {package.Name} installer.", exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"The download for {package.Name} timed out. Try again.");
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or
                InvalidDataException or UnauthorizedAccessException or Win32Exception)
            {
                throw new InvalidOperationException($"{package.Name} setup stopped. {exception.Message}", exception);
            }

            if (result is null) continue;
            if (result is 1602 or 1223)
                throw new OperationCanceledException($"The {package.Name} installer was canceled.");
            if (result is not (0 or 3010 or 1641))
                throw new InvalidOperationException($"{package.Name} exited with code {result}. Check the installer and try again.");

            if (result == 0 && IsMissing())
                throw new InvalidOperationException(
                    $"Windows could not confirm {package.Name} was installed. Restart if the vendor requested it, then check again.");

            if (result is 3010 or 1641 || package.RestartAfterInstall) _pendingRestart.Add(package.Id);
            if (result == 1641) break; // The vendor has initiated a Windows restart.
        }
    }

    private PrerequisiteItem Inspect(PrerequisitePackage package)
    {
        var (state, detail) = _pendingRestart.Contains(package.Id)
            ? (PrerequisiteState.RestartRequired, "Restart Windows to finish installing this tool.")
            : Detect(package.Id);
        if (state == PrerequisiteState.Missing && !_supportsAutomaticInstall)
        {
            state = PrerequisiteState.NeedsAttention;
            detail = "Automatic setup uses x64 Windows packages. Open the vendor page for supported downloads.";
        }
        return new(package.Name, package.Version, package.Source, state, detail ?? package.Purpose);
    }

    private (PrerequisiteState, string?) Detect(PrerequisiteId id)
    {
        try { return _detect(id); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or Win32Exception)
        {
            return (PrerequisiteState.NeedsAttention, "Windows could not check the existing installation.");
        }
    }

    private static async Task<int?> DownloadAndInstallAsync(PrerequisitePackage package,
        Func<bool> isMissing, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var temporary = Directory.CreateTempSubdirectory("HapticBridge-Setup-");
        try
        {
            var url = new Uri(package.Url);
            string download = Path.Combine(temporary.FullName, Path.GetFileName(url.AbsolutePath));
            progress.Report($"Downloading {package.Name} {package.Version}…");
            await DownloadAsync(url, download, cancellationToken).ConfigureAwait(false);
            await VerifySha256Async(download, package.Sha256, cancellationToken).ConfigureAwait(false);

            string installer = download;
            if (package.ArchiveInstaller is not null)
            {
                string extracted = Path.Combine(temporary.FullName, "vendor");
                ZipFile.ExtractToDirectory(download, extracted);
                installer = Path.Combine(extracted, package.ArchiveInstaller);
                if (!File.Exists(installer))
                    throw new InvalidDataException("The verified archive did not contain its expected installer.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!isMissing()) return null;
            if (package.Portable)
            {
                progress.Report($"Installing {package.Name} for your Windows account…");
                PlacePortable(download, SoundSyncIntegration.ManagedExecutablePath);
                return 0;
            }

            // HttpClient does not apply the internet zone used by Windows Attachment Manager.
            File.WriteAllText(installer + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report($"Finish the {package.Name} installer in its own window. Choose restart later if offered.");
            var startInfo = new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer)!,
                Verb = package.ArchiveInstaller is null ? "" : "runas"
            };
            using var process = Process.Start(startInfo) ?? throw new IOException("Windows did not start the installer.");
            // Let the vendor exit normally so its files stay available and drivers are not left half-installed.
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            try { temporary.Delete(recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not remove prerequisite download files: {0}", exception.Message);
            }
        }
    }

    private static async Task DownloadAsync(Uri url, string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        using var response = await Downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumPackageBytes)
            throw new InvalidDataException("The download exceeds the supported package size.");

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaximumPackageBytes)
                throw new InvalidDataException("The download exceeds the supported package size.");
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
        }
    }

    internal static async Task VerifySha256Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download failed its integrity check. It was not opened.");
    }

    internal static void PlacePortable(string download, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(download, staged);
            File.WriteAllText(staged + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            File.Move(staged, destination);
        }
        finally { File.Delete(staged); }
    }
}
