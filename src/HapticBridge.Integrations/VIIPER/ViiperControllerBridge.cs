using System.Net.Sockets;
using HapticBridge.Integrations.Apollo;

namespace HapticBridge.Integrations.VIIPER;

public interface IViiperControllerBridge : IDisposable
{
    bool IsConnected { get; }
    int AudioPort { get; }
    ViiperUsbipPortManager PortManager { get; }
    bool TryConnect(string host = "127.0.0.1", int apiPort = 3242);
    void Disconnect();
    bool SubmitInput(DualsenseInput input);
    bool SubmitDs4Report(Ds4ReportEx report);
}

/// <summary>
/// Experimental TCP reachability probe. A connection alone does not create or own a VIIPER device.
/// </summary>
public class ViiperControllerBridge : IViiperControllerBridge
{
    private readonly object _lock = new();
    private bool _disposed;
    private bool _isConnected;
    private TcpClient? _tcpClient;

    public bool IsConnected
    {
        get { lock (_lock) return _isConnected; }
    }

    public int AudioPort => 0;

    public ViiperUsbipPortManager PortManager { get; } = new();

    public bool TryConnect(string host = "127.0.0.1", int apiPort = 3242)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (_isConnected) return true;

            var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                client.ConnectAsync(host, apiPort, timeout.Token).AsTask().GetAwaiter().GetResult();
                _tcpClient = client;
                _isConnected = true;
                return true;
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
            {
                client.Dispose();
                return false;
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _tcpClient?.Dispose();
            _tcpClient = null;
            _isConnected = false;
        }
    }

    // Until a device is created and the versioned stream is implemented, no input is submitted.
    public bool SubmitInput(DualsenseInput input) => false;

    public bool SubmitDs4Report(Ds4ReportEx report) => false;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Disconnect();
            _disposed = true;
        }
    }
}
