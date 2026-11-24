using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HapticBridge.Audio.Wasapi;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;

namespace HapticBridge.Setup.Diagnostics;

public class DiagnosticsService : IDiagnosticsService
{
    private readonly IAudioEndpointService _audio;
    private readonly IVoicemeeterManager _voicemeeter;
    private readonly IComponentDetectionService _components;
    private readonly IControllerDetectionService _controllers;
    private readonly WasapiLoopbackRoute _nativeAudio = new();

    public DiagnosticsService(
        IAudioEndpointService audio,
        IVoicemeeterManager voicemeeter,
        IComponentDetectionService components,
        IControllerDetectionService controllers)
    {
        _audio = audio;
        _voicemeeter = voicemeeter;
        _components = components;
        _controllers = controllers;
    }

    public async Task<string> GenerateDiagnosticReportJsonAsync()
    {
        var endpoints = _audio.GetAllRenderEndpoints();
        var data = new Dictionary<string, object>
        {
            ["TimestampUtc"] = DateTime.UtcNow,
            ["OS"] = new
            {
                Version = Environment.OSVersion.ToString(),
                Is64Bit = Environment.Is64BitOperatingSystem,
                Machine = Environment.MachineName
            },
            ["Controllers"] = _controllers.DetectControllers(),
            ["Components"] = await _components.DetectAllComponentsAsync(),
            ["AudioEndpoints"] = endpoints.Select(e => new
            {
                e.Id,
                e.FriendlyName,
                e.EndpointType,
                e.IsActive,
                e.IsDefault,
                e.VolumeLevel
            }).ToList(),
            ["VoicemeeterRouting"] = _voicemeeter.GetLiveRoutingState(),
            ["NativeAudioFormatProbe"] = InspectNativeAudio(endpoints)
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> GenerateDiagnosticReportTextAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("                       HAPTICBRIDGE SYSTEM DIAGNOSTIC REPORT                    ");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS Version: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine();

        sb.AppendLine("--- 1. CONTROLLER STATUS ---");
        var ctrl = _controllers.DetectControllers();
        sb.AppendLine($"Main Status: {ctrl.MainStatus}");
        sb.AppendLine($"Subtitle:    {ctrl.Subtitle}");
        sb.AppendLine($"Direct USB Kishi detected: {ctrl.IsKishiDetected}");
        sb.AppendLine($"Remote Apollo session: {ctrl.PipelineStatus.IsApolloSessionActive?.ToString() ?? "Unverified"}");
        sb.AppendLine($"ViGEm DS4 candidates: {ctrl.PipelineStatus.VirtualDS4CandidateCount}");
        sb.AppendLine($"USB/IP DualSense detected: {ctrl.IsVirtualDualSenseDetected}");
        sb.AppendLine();

        sb.AppendLine("--- 2. SYSTEM COMPONENTS ---");
        var comps = await _components.DetectAllComponentsAsync();
        foreach (var c in comps)
        {
            sb.AppendLine($"[{c.Health}] {c.Name.PadRight(22)} : {c.StatusText.PadRight(18)} | {c.Details}");
        }
        sb.AppendLine();

        sb.AppendLine("--- 3. AUDIO PLAYBACK ENDPOINTS ---");
        var endpoints = _audio.GetAllRenderEndpoints();
        foreach (var e in endpoints.Where(x => x.IsActive))
        {
            string defBadge = e.IsDefault ? " [DEFAULT]" : "";
            sb.AppendLine($"• {e.FriendlyName.PadRight(35)} [{e.EndpointType}]{defBadge} (Vol: {(int)(e.VolumeLevel * 100)}%)");
            sb.AppendLine($"  Endpoint ID: {e.Id}");
        }
        var nativeAudio = InspectNativeAudio(endpoints);
        sb.AppendLine();
        sb.AppendLine("Virtual DualSense loopback format check");
        sb.AppendLine(nativeAudio.Detail);
        if (nativeAudio.SourceFormat is not null) sb.AppendLine($"  DualSense  {nativeAudio.SourceFormat}");
        if (nativeAudio.DestinationFormat is not null) sb.AppendLine($"  VB-CABLE  {nativeAudio.DestinationFormat}");
        if (nativeAudio.DestinationChannels.Length > 0)
            sb.AppendLine($"  Destination channels (1-based)  {string.Join(", ", nativeAudio.DestinationChannels)}");
        sb.AppendLine();

        sb.AppendLine("--- 4. VOICEMEETER LIVE ROUTING ---");
        var vm = _voicemeeter.GetLiveRoutingState();
        sb.AppendLine($"Voicemeeter Installed: {_voicemeeter.IsInstalled}");
        sb.AppendLine($"Voicemeeter Running:   {_voicemeeter.IsRunning}");
        sb.AppendLine($"API Connected:         {vm.IsConnected}");
        sb.AppendLine($"Reference readings:   {(vm.HasReferenceParameters ? "Complete" : "Unverified")}");
        sb.AppendLine($"Voicemeeter Type:      {vm.VoicemeeterType} (2=Banana)");
        sb.AppendLine($"Strip[3] VAIO -> A1:   {vm.StripVaioA1}");
        sb.AppendLine($"Strip[3] VAIO -> A2:   {vm.StripVaioA2}");
        sb.AppendLine($"Strip[3] VAIO -> B1:   {vm.StripVaioB1}");
        sb.AppendLine($"Strip[3] VAIO -> B2:   {vm.StripVaioB2}");
        sb.AppendLine($"Bus A2 Composite:      {vm.BusA2Composite}");
        sb.AppendLine($"Patch Composite Ch 1:  {vm.PatchCompositeCh1} (Expected: 11)");
        sb.AppendLine($"Patch Composite Ch 2:  {vm.PatchCompositeCh2} (Expected: 12)");
        sb.AppendLine($"Bus A1 Physical Dev:   {vm.PhysicalOutA1Device}");
        sb.AppendLine($"Bus A2 Cable Dev:      {vm.PhysicalOutA2Device}");
        sb.AppendLine($"Routing Compliant:     {vm.IsProperlyConfigured}");
        sb.AppendLine();

        sb.AppendLine("--- 5. TROUBLESHOOTING NOTES ---");
        if (!vm.HasReferenceParameters)
        {
            sb.AppendLine("• Voicemeeter routing could not be inspected. Confirm that Voicemeeter Banana is running and its Remote API is available.");
        }
        else if (!vm.IsProperlyConfigured)
        {
            sb.AppendLine("• Voicemeeter routing does not match reference haptic setup. Run Setup / Repair to reconfigure.");
        }
        if (!ctrl.IsVirtualDualSenseDetected)
        {
            sb.AppendLine("• Virtual DualSense is missing. Ensure DS4Windows is running with 'Moonlight/Sunshine virtual controller support' enabled and emulated controller set to DualSense.");
        }
        var cable = _audio.GetVirtualCableEndpoint();
        if (cable == null || !cable.IsActive)
        {
            sb.AppendLine("• Windows has no active VB-CABLE playback endpoint. Inspect the endpoint and driver state before making changes.");
        }

        return sb.ToString();
    }

    public async Task<string> ExportDiagnosticReportTextAsync(string exportPath)
    {
        string reportText = await GenerateDiagnosticReportTextAsync();
        var directory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(exportPath, reportText, Encoding.UTF8);

        return reportText;
    }

    private WasapiRouteProbe InspectNativeAudio(IReadOnlyList<AudioEndpointModel> endpoints)
    {
        var source = endpoints.Where(e => e.IsActive && e.EndpointType == AudioEndpointType.VirtualDualSense).Take(2).ToArray();
        var cables = endpoints.Where(e => e.IsActive && e.EndpointType == AudioEndpointType.VirtualCable).ToArray();
        var destination = cables.Length == 1 ? cables : cables.Where(e =>
            e.FriendlyName.StartsWith("CABLE In 16 Ch (", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (source.Length != 1 || destination.Length != 1)
        {
            WasapiMixFormat? destinationFormat = null;
            string detail = $"The format check needs one active DualSense and one identifiable VB-CABLE playback endpoint. Found {source.Length}{(source.Length == 2 ? "+" : "")} DualSense and {cables.Length} cable candidates.";
            if (destination.Length == 1)
            {
                try { destinationFormat = _nativeAudio.ReadMixFormat(destination[0].Id); }
                catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or FormatException or NotSupportedException)
                {
                    detail += $" {exception.Message}";
                }
            }
            return new(null, destinationFormat, [], detail);
        }

        return _nativeAudio.Probe(source[0], destination[0]);
    }
}
