using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;

namespace HapticBridge.Integrations.Apollo;

public enum ApolloAudioMode { Unreachable, Unsupported, Available, Enabled }

public sealed record ApolloHostStatus(Uri WebUi, ApolloAudioMode AudioMode, bool IsInstalled)
{
    public string? ConfigurationPath { get; init; }
    public string? CertificatePath { get; init; }
    public string? TrustedInstalledExecutable { get; init; }
    public bool IsRunning { get; init; }
    public bool CanStart { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public string? HostName { get; init; }
}

public sealed class ApolloHostClient(ApolloIntegration apollo)
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(2),
        MaxResponseContentBufferSize = 64 * 1024
    };

    public async Task<ApolloHostStatus> InspectAsync()
    {
        var runningExecutable = ApolloIntegration.RunningHostExecutable(out bool running);
        string installedExecutable = ApolloIntegration.InstalledExecutable;
        var source = string.IsNullOrEmpty(runningExecutable) ||
            string.Equals(runningExecutable, installedExecutable, StringComparison.OrdinalIgnoreCase)
            ? apollo : new RunningApolloConfiguration(runningExecutable);
        var config = source.ReadConfiguration();
        string? configDirectory = Path.IsPathFullyQualified(source.ConfigurationPath)
            ? Path.GetDirectoryName(source.ConfigurationPath)
            : Path.IsPathFullyQualified(installedExecutable)
                ? Path.Combine(Path.GetDirectoryName(installedExecutable)!, "config") : null;
        var (serverInfo, webUi) = GetLocalEndpoints(config.RawSettings.GetValueOrDefault("port"));
        bool installed = apollo.IsInstalled;
        var status = new ApolloHostStatus(webUi, ApolloAudioMode.Unreachable, installed)
        {
            IsRunning = running,
            ConfigurationPath = configDirectory is null ? null :
                Path.IsPathFullyQualified(source.ConfigurationPath) ? source.ConfigurationPath : Path.Combine(configDirectory, "sunshine.conf"),
            CertificatePath = configDirectory is null ? null : ResolveCertificatePath(configDirectory, config),
            TrustedInstalledExecutable = TrustedInstalledExecutable(installedExecutable, runningExecutable),
            CanStart = Path.IsPathFullyQualified(installedExecutable) && File.Exists(installedExecutable),
            Addresses = ReadLanAddresses(serverInfo.Port)
        };
        try
        {
            string response = await Client.GetStringAsync(serverInfo).ConfigureAwait(false);
            var root = XDocument.Parse(response).Root;
            var mode = ReadAudioMode(root);
            return status with
            {
                AudioMode = mode,
                IsRunning = running || mode != ApolloAudioMode.Unreachable,
                HostName = mode == ApolloAudioMode.Unreachable ? null : (string?)root?.Element("hostname")
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Xml.XmlException)
        {
            return status;
        }
    }

    private static string? TrustedInstalledExecutable(string installed, string? running)
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(programFiles)) return null;
        string expected = Path.Combine(programFiles, "Apollo", "sunshine.exe");
        return string.Equals(expected, installed, StringComparison.OrdinalIgnoreCase) &&
            (running is null || string.Equals(expected, running, StringComparison.OrdinalIgnoreCase))
            ? expected : null;
    }

    public void StartInstalledHost()
    {
        ApolloIntegration.RunningHostExecutable(out bool running);
        if (running) return;
        string executable = ApolloIntegration.InstalledExecutable;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Apollo was not found. Install it from Check PC setup.");
        using var process = Process.Start(ApolloIntegration.CreateStartInfo());
        if (process is null) throw new InvalidOperationException("Windows did not start Apollo.");
        if (!process.WaitForExit(15000)) return;
        if (process.ExitCode != 0 && process.ExitCode != 1056)
            throw new InvalidOperationException("Windows could not start Apollo’s service. Check that Apollo is installed and its service is enabled.");
    }

    private static string? ResolveCertificatePath(string directory, ApolloConfigModel config)
    {
        try
        {
            var configured = config.RawSettings.GetValueOrDefault("cert");
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine("credentials", "cacert.pem") : configured, directory);
        }
        catch (ArgumentException) { return null; }
    }

    private sealed class RunningApolloConfiguration(string executable) : ApolloIntegration
    {
        public override string ConfigurationPath => Path.Combine(Path.GetDirectoryName(executable)!, "config", "sunshine.conf");
    }

    private static IReadOnlyList<string> ReadLanAddresses(int port)
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
                .Select(adapter => adapter.GetIPProperties())
                .ToArray();
            var routed = adapters.Where(properties => properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))).ToArray();
            return (routed.Length > 0 ? routed : adapters)
                .SelectMany(properties => properties.UnicastAddresses.Select(address => address.Address))
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) &&
                    !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()
                .Take(4)
                .Select(address => FormatLanAddress(address, port))
                .ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    internal static string FormatLanAddress(IPAddress address, int port) =>
        port == 47989 ? address.ToString() : new IPEndPoint(address, port).ToString();

    internal static (Uri ServerInfo, Uri WebUi) GetLocalEndpoints(string? savedPort)
    {
        const int defaultPort = 47989;
        int port = int.TryParse(savedPort, out int configured) && configured is >= 1019 and <= 65514
            ? configured : defaultPort;
        return (new Uri($"http://127.0.0.1:{port}/serverinfo"), new Uri($"https://localhost:{port + 1}/"));
    }

    internal static ApolloAudioMode ReadAudioMode(string serverInfo) => ReadAudioMode(XDocument.Parse(serverInfo).Root);

    private static ApolloAudioMode ReadAudioMode(XElement? root)
    {
        if (root?.Name != "root" || root.Attribute("status_code")?.Value != "200" || root.Element("PairStatus") is null)
            return ApolloAudioMode.Unreachable;

        return (string?)root.Element("HapticBridgeRemoteInputAudio") switch
        {
            "enabled" => ApolloAudioMode.Enabled,
            "disabled" => ApolloAudioMode.Available,
            _ => ApolloAudioMode.Unsupported
        };
    }
}
