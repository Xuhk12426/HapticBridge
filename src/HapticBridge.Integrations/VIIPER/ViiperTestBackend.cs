using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HapticBridge.Integrations.DS4Windows;

namespace HapticBridge.Integrations.VIIPER;

internal interface IViiperTestBackend : IDisposable
{
    int ApiPort { get; }
    bool HasExited { get; }
}

internal sealed class ViiperTestBackend(Process process, DirectoryInfo workingDirectory, int apiPort) : IViiperTestBackend
{
    public int ApiPort => apiPort;
    public bool HasExited => process.HasExited;

    public static IViiperTestBackend Start()
    {
        string install = DS4WindowsIntegration.LocateInstallDirectory();
        string executable = Path.Combine(install, "VIIPER", "viiper.exe");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The installed VIIPER executable is missing.");
        if (FileVersionInfo.GetVersionInfo(executable).ProductVersion?.TrimStart('v') != ViiperControllerTest.TestedVersion)
            throw new NotSupportedException($"The controller test requires VIIPER {ViiperControllerTest.TestedVersion}.");

        var directory = Directory.CreateTempSubdirectory("HapticBridge-VIIPER-");
        try
        {
            string configPath = Path.Combine(directory.FullName, "server.json");
            string keyPath = Path.Combine(directory.FullName, "key.txt");
            File.WriteAllText(configPath, "{}");
            File.WriteAllText(keyPath, RandomNumberGenerator.GetHexString(64));

            // Reserve both ports at once so they cannot be the same. The helper releases
            // the reservations before the child process binds to these loopback addresses.
            var (usbPort, localApiPort) = FindLocalPorts();
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory.FullName
            };
            foreach (string argument in new[]
            {
                "server", "--config-only", $"--config={configPath}", $"--key-file={keyPath}",
                $"--usb.addr=127.0.0.1:{usbPort}", $"--api.addr=127.0.0.1:{localApiPort}",
                "--api.auto-attach-local-client=true", "--api.auto-attach-windows-native=true",
                "--api.require-local-host-auth=false"
            })
                startInfo.ArgumentList.Add(argument);

            // Prevent settings intended for an existing server from affecting this test.
            foreach (string name in startInfo.Environment.Keys.Where(name => name.StartsWith("VIIPER_", StringComparison.OrdinalIgnoreCase)).ToArray())
                startInfo.Environment.Remove(name);

            var child = Process.Start(startInfo) ?? throw new IOException("Windows could not start the VIIPER test backend.");
            return new ViiperTestBackend(child, directory, localApiPort);
        }
        catch
        {
            DeleteWorkspace(directory);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                if (!process.WaitForExit(2000))
                    Trace.TraceWarning("The VIIPER test backend did not exit after termination.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Trace.TraceWarning("Could not stop the VIIPER test backend: {0}", exception);
        }
        finally
        {
            process.Dispose();
            DeleteWorkspace(workingDirectory);
        }
    }

    private static (int UsbPort, int ApiPort) FindLocalPorts()
    {
        using var usb = new TcpListener(IPAddress.Loopback, 0);
        using var api = new TcpListener(IPAddress.Loopback, 0);
        usb.Start();
        api.Start();
        return (((IPEndPoint)usb.LocalEndpoint).Port, ((IPEndPoint)api.LocalEndpoint).Port);
    }

    private static void DeleteWorkspace(DirectoryInfo directory)
    {
        try
        {
            File.Delete(Path.Combine(directory.FullName, "server.json"));
            File.Delete(Path.Combine(directory.FullName, "key.txt"));
            directory.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not clean up temporary VIIPER configuration: {0}", exception);
        }
    }
}
