using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HapticBridge.Integrations.Apollo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class ApolloAdminTests
{
    [TestMethod]
    public void TestHostImportRejectsUnknownZipBeforeCreatingDestination()
    {
        string file = Path.GetTempFileName();
        string destination = file + ".host";
        try
        {
            File.WriteAllText(file, "Unapproved archive");
            Assert.ThrowsException<InvalidDataException>(() => ApolloAudioTestHost.Import(file, destination));
            Assert.IsFalse(Directory.Exists(destination));
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task NativeControlsUseOnlyTheExpectedApolloCommands()
    {
        var routes = new List<(string Path, string Body)>();
        using var client = new ApolloAdminClient(new HttpClient(new FakeApollo(async request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            routes.Add((path, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(path == "/api/clients/list"
                    ? """{"status":true,"named_certs":[{"uuid":"phone","name":"Artemis","connected":true},{"uuid":"tablet","name":"Old tablet","connected":false}]}"""
                    : """{"status":true}""")
            };
        })) { BaseAddress = new Uri("https://localhost:47990/") });

        await client.SignInAsync("test", "fixture");
        var devices = await client.ReadDevicesAsync();
        Assert.IsTrue(devices[0].Connected);
        Assert.IsFalse(devices[1].Connected);
        await Assert.ThrowsExceptionAsync<ArgumentException>(async () => await client.PairAsync("01x2", "Phone"));
        await client.PairAsync("0123", "Phone");
        await client.DisconnectAsync(devices[0].Id);
        await client.ForgetAsync(devices[1].Id);
        await client.StopAsync();
        CollectionAssert.AreEqual(new[] { "/api/login", "/api/clients/list", "/api/pin",
            "/api/clients/disconnect", "/api/clients/unpair", "/api/quit" }, routes.Select(route => route.Path).ToArray());
        StringAssert.Contains(routes[2].Body, "\"pin\":\"0123\"");
        StringAssert.Contains(routes[4].Body, "\"uuid\":\"tablet\"");
    }

    [TestMethod]
    public async Task RejectedSignInAndRedirectDoNotBecomeSuccessfulRequests()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Found })
        {
            using var client = new ApolloAdminClient(new HttpClient(new FakeApollo(_ =>
                Task.FromResult(new HttpResponseMessage(status)))) { BaseAddress = new Uri("https://localhost:47990/") });
            await Assert.ThrowsExceptionAsync<ApolloAuthenticationException>(() => client.StopAsync());
        }
    }

    [TestMethod]
    public void LocalCertificatePinRejectsUnexpectedOrExpiredCertificates()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Apollo", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.Now;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var pin = certificate.GetCertHash(HashAlgorithmName.SHA256);
        Assert.IsTrue(ApolloAdminClient.MatchesCertificate(certificate, pin, now.LocalDateTime));
        Assert.IsFalse(ApolloAdminClient.MatchesCertificate(certificate, pin, now.AddDays(2).LocalDateTime));
        pin[0] ^= 1;
        Assert.IsFalse(ApolloAdminClient.MatchesCertificate(certificate, pin, now.LocalDateTime));
        Assert.ThrowsException<ArgumentException>(() => new ApolloAdminClient(new Uri("https://192.168.1.5"), "unused"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProtectedCertificateFallbackVerifiesTheWindowsPeerBeforeTls(bool dualStack)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Apollo", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),
            null, X509KeyStorageFlags.UserKeySet);
        using var listener = new TcpListener(dualStack ? IPAddress.IPv6Any : IPAddress.Loopback, 0);
        if (dualStack) listener.Server.DualMode = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = ServeAsync();
        var host = new ApolloHostStatus(new Uri($"https://localhost:{port}/"), ApolloAudioMode.Unsupported, true)
        {
            TrustedInstalledExecutable = Environment.ProcessPath
        };
        using var client = new ApolloAdminClient(host);
        IReadOnlyList<ApolloPairedDevice> devices;
        try { devices = await client.ReadDevicesAsync(); }
        catch (HttpRequestException)
        {
            if (serve.IsFaulted) await serve;
            throw;
        }
        Assert.HasCount(0, devices);
        await serve;

        using var wrongHost = new ApolloAdminClient(host with { TrustedInstalledExecutable = @"C:\Wrong\sunshine.exe" });
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => wrongHost.ReadDevicesAsync());

        async Task ServeAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var tls = new SslStream(peer.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            using var reader = new StreamReader(tls, leaveOpen: true);
            string? firstLine = await reader.ReadLineAsync(timeout.Token);
            StringAssert.StartsWith(firstLine ?? "", "GET /api/clients/list ");
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            const string body = """{"status":true,"named_certs":[]}""";
            byte[] response = System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await tls.WriteAsync(response, timeout.Token);
        }
    }

    [TestMethod]
    public void Ds4SetupOnlyChangesTheControllerAndBacksUpOnce()
    {
        string path = Path.GetTempFileName();
        try
        {
            const string original = "gamepad = x360 # controller\r\nvirtual_sink = VAIO\r\n";
            File.WriteAllText(path, original);
            Assert.ThrowsException<IOException>(() => ApolloIntegration.SaveDs4(path, _ => ""));
            Assert.AreEqual(original, File.ReadAllText(path));
            ApolloIntegration.SaveDs4(path, source =>
            {
                Assert.AreEqual(original, File.ReadAllText(source));
                return "fixture backup";
            });
            Assert.AreEqual("gamepad = ds4 # controller\r\nvirtual_sink = VAIO\r\n", File.ReadAllText(path));
            ApolloIntegration.SaveDs4(path, _ => throw new AssertFailedException("Unchanged settings need no backup."));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ControllerAudioEditRequiresBackupAndPreservesUnrelatedSettings()
    {
        string path = Path.GetTempFileName();
        try
        {
            const string original = "# preserve this\r\ngamepad = ds4\r\nremote_input_audio = disabled # keep note\r\n";
            File.WriteAllText(path, original);
            Assert.ThrowsException<IOException>(() => ApolloIntegration.SaveControllerAudio(path, true, true, _ => ""));
            Assert.AreEqual(original, File.ReadAllText(path));
            ApolloIntegration.SaveControllerAudio(path, true, true, source =>
            {
                Assert.AreEqual(original, File.ReadAllText(source));
                return "fixture backup";
            });
            var text = File.ReadAllText(path);
            StringAssert.Contains(text, "# preserve this\r\ngamepad = ds4\r\n");
            StringAssert.Contains(text, "remote_input_audio = enabled # keep note");
            var config = ApolloIntegration.ReadHostConfiguration(path);
            Assert.IsTrue(config.IsEnabled("remote_input_haptics"));
            Assert.IsTrue(config.IsEnabled("enable_input_only_mode"));
            Assert.IsTrue(config.IsEnabled("stream_audio"));
        }
        finally { File.Delete(path); }
    }

    private sealed class FakeApollo(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request);
    }
}
