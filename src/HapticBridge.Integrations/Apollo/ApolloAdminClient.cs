using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HapticBridge.Integrations.Apollo;

public sealed record ApolloPairedDevice(string Id, string Name, bool Connected)
{
    public string Label => $"{Name} · {(Connected ? "Connected" : "Paired, offline")}";
}

/// <summary>One authenticated session with a certificate-pinned local Apollo host.</summary>
public sealed class ApolloAdminClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CookieContainer? _cookies;

    public ApolloAdminClient(Uri webUi, string certificatePath)
        : this(webUi, certificatePath, null) { }

    public ApolloAdminClient(ApolloHostStatus host)
        : this(host.WebUi, host.CertificatePath, host.TrustedInstalledExecutable) { }

    private ApolloAdminClient(Uri webUi, string? certificatePath, string? installedExecutable)
    {
        if (webUi.Scheme != Uri.UriSchemeHttps || !webUi.IsLoopback)
            throw new ArgumentException("Apollo administration requires local HTTPS.", nameof(webUi));

        byte[]? pin = null;
        if (certificatePath is not null)
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
                pin = certificate.GetCertHash(HashAlgorithmName.SHA256);
            }
            catch (Exception exception) when (installedExecutable is not null &&
                exception is IOException or UnauthorizedAccessException)
            {
                // An installed service can protect its certificate directory from standard users.
            }
        }
        if (pin is null && installedExecutable is null)
            throw new IOException("Apollo’s public certificate could not be read.");

        _cookies = new CookieContainer();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CookieContainer = _cookies
        };
        if (pin is null)
        {
            handler.ConnectCallback = (context, cancellation) =>
                ApolloLocalPeer.ConnectAsync(context, webUi.Port, installedExecutable!, cancellation);
        }
        handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) =>
        {
            if (remote is null) return false;
            using var imported = remote is X509Certificate2 ? null : X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
            var certificate = remote as X509Certificate2 ?? imported!;
            var now = DateTime.Now;
            if (now < certificate.NotBefore || now > certificate.NotAfter) return false;
            var actual = certificate.GetCertHash(HashAlgorithmName.SHA256);
            // An initial pin may be learned only after the TCP callback verifies installed Apollo.
            var expected = Interlocked.CompareExchange(ref pin, actual, null) ?? actual;
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = webUi,
            Timeout = TimeSpan.FromSeconds(7),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
    }

    internal ApolloAdminClient(HttpClient http) => _http = http;

    internal static bool MatchesCertificate(X509Certificate2 certificate, byte[] expectedHash, DateTime now) =>
        now >= certificate.NotBefore && now <= certificate.NotAfter &&
        CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), expectedHash);

    public async Task SignInAsync(string username, string password)
    {
        using var response = await _http.PostAsJsonAsync("/api/login", new { username, password }).ConfigureAwait(false);
        CheckResponse(response);
        if (_cookies is not null && _cookies.GetCookies(_http.BaseAddress!)["auth"] is null)
            throw new InvalidOperationException("Apollo did not create a sign-in session.");
    }

    public async Task<IReadOnlyList<ApolloPairedDevice>> ReadDevicesAsync()
    {
        using var response = await _http.GetAsync("/api/clients/list").ConfigureAwait(false);
        CheckResponse(response);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (!IsSuccessful(payload.RootElement) ||
            !payload.RootElement.TryGetProperty("named_certs", out var devices) || devices.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Apollo did not return a paired-device list.");

        var result = new List<ApolloPairedDevice>();
        foreach (var item in devices.EnumerateArray())
        {
            string? id = ReadString(item, "uuid");
            if (string.IsNullOrEmpty(id)) continue;
            var name = ReadString(item, "name");
            bool connected = item.TryGetProperty("connected", out var state) && state.ValueKind == JsonValueKind.True;
            result.Add(new(id, string.IsNullOrWhiteSpace(name) ? "Unnamed device" : name, connected));
        }
        return result;
    }

    public Task PairAsync(string pin, string name)
    {
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
            throw new ArgumentException("Enter the four-digit PIN shown in Artemis.");
        return PostCheckedAsync("/api/pin", new { pin, name },
            "Apollo declined the PIN. Select this PC in Artemis again and use the PIN it currently shows.");
    }

    public Task ForgetAsync(string id) =>
        PostCheckedAsync("/api/clients/unpair", new { uuid = id }, "Apollo could not forget this device.");

    public Task DisconnectAsync(string id) =>
        PostCheckedAsync("/api/clients/disconnect", new { uuid = id }, "Apollo could not disconnect this device.");

    public async Task StopAsync()
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/quit", new { }).ConfigureAwait(false);
            CheckResponse(response);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is
            HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError)
        {
            // Apollo can exit before sending a response. The caller must verify that it stopped.
        }
        catch (OperationCanceledException)
        {
            // The caller verifies whether the local host has stopped.
        }
    }

    private async Task PostCheckedAsync<T>(string route, T values, string failure)
    {
        using var response = await _http.PostAsJsonAsync(route, values).ConfigureAwait(false);
        CheckResponse(response);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (!IsSuccessful(payload.RootElement)) throw new InvalidOperationException(failure);
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ApolloAuthenticationException("Apollo requires sign-in. Check your Apollo username and password.");
        if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther)
            throw new ApolloAuthenticationException("Apollo needs its first-time web setup. Use Open Apollo web UI to finish it.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Apollo could not complete the request (HTTP {(int)response.StatusCode}).");
    }

    private static bool IsSuccessful(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    public void Dispose() => _http.Dispose();
}

public sealed class ApolloAuthenticationException(string message) : Exception(message);
