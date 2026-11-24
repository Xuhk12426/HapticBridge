using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace HapticBridge.Integrations.Apollo;

public static class ApolloAudioTestHost
{
    public const string ArchiveName = "Apollo-Controller-Audio-0.4.6-test.2-win-x64.zip";
    private const string ArchiveHash = "5E877D7CE9D9ED675953130B5B036EBA7F9548211EBC8AB0C9D79639D67027CF";
    private const string ExecutableHash = "87BEC108A3067B352914951FA21C98A8E5E1DE7175C9D58866CA4D263243D738";
    private const string CompanionDllHash = "11CD91171765CD65F1C3362E3DF67E5F5A618A6B88C8CDEC899B802EAF1C7B0D";

    private static string HostDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HapticBridge", "Apollo", "0.4.6-test.2");

    public static bool IsImported => File.Exists(Path.Combine(HostDirectory, "sunshine.exe"));

    public static void Import(string archive) => Import(archive, HostDirectory);

    internal static void Import(string archive, string destination)
    {
        using var source = File.OpenRead(archive);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(source)), ArchiveHash, StringComparison.Ordinal))
            throw new InvalidDataException("This ZIP does not match the approved Controller + Audio test build.");

        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        if (Directory.Exists(destination))
            throw new IOException("The test host folder already exists. Its files and pairing settings were kept.");

        string staging = Path.Combine(parent, $".import-{Guid.NewGuid():N}");
        try
        {
            source.Position = 0;
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            zip.ExtractToDirectory(staging);
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static void Start()
    {
        ApolloIntegration.RunningHostExecutable(out bool running);
        if (running) throw new InvalidOperationException("Stop the running Apollo host before starting the test host.");

        string executable = Path.Combine(HostDirectory, "sunshine.exe");
        using var source = File.OpenRead(executable);
        using var library = File.OpenRead(Path.Combine(HostDirectory, "zlib1.dll"));
        if (Convert.ToHexString(SHA256.HashData(source)) != ExecutableHash ||
            Convert.ToHexString(SHA256.HashData(library)) != CompanionDllHash)
            throw new InvalidDataException("The imported test host binaries have changed. Check your copy before running it.");

        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = HostDirectory,
            UseShellExecute = true
        });
        if (process is null) throw new InvalidOperationException("Windows could not start the test host.");
    }
}
