using HapticBridge.Core.Models;

namespace HapticBridge.Core.Services;

public interface IAudioEndpointService
{
    IReadOnlyList<AudioEndpointModel> GetAllRenderEndpoints();
    IReadOnlyList<AudioEndpointModel> GetPhysicalPlaybackEndpoints();
    (float Level, bool IsMuted)? SetPhysicalPlaybackVolume(string endpointId, int volumePercent);
    AudioEndpointModel? GetDefaultPlaybackEndpoint();
    AudioEndpointModel? GetVirtualDualSenseEndpoint();
    AudioEndpointModel? GetVirtualCableEndpoint();
    AudioEndpointModel? GetVoicemeeterVaioEndpoint();
    bool SetDefaultEndpoint(string endpointId);
}

public interface IVoicemeeterManager
{
    bool IsInstalled { get; }
    bool IsRunning { get; }
    bool CanLaunchBanana { get; }
    string DllPath { get; }
    VoicemeeterRoutingState GetLiveRoutingState();
    (string DeviceName, int? VolumePercent)? GetListeningOutput();
    bool TrySetPhysicalOutput(string deviceName);
    bool TrySetListeningVolume(string deviceName, int volumePercent);
    bool ConfigureReferenceRouting(string? physicalOutputA1Name, string cableOutputA2Name);
    bool CreateConfigurationBackup(string backupDestinationPath);
    bool RestoreConfiguration(string backupSourcePath);
    void LaunchVoicemeeter();
}

public interface IComponentDetectionService
{
    Task<IReadOnlyList<SystemComponentItem>> DetectAllComponentsAsync();
    SystemComponentItem DetectApollo();
    SystemComponentItem DetectDS4Windows();
    SystemComponentItem DetectVoicemeeter();
    SystemComponentItem DetectVbCable();
    SystemComponentItem DetectSoundSync();
}

public interface IControllerDetectionService
{
    ControllerDetectionResult DetectControllers();
}

public interface IConfigurationBackupService
{
    string CreateBackup(string componentName, string sourceFilePath);
    bool RestoreBackup(string backupFilePath, string targetDestinationPath);
    IReadOnlyList<string> ListBackups(string componentName);
}

public interface IDiagnosticsService
{
    Task<string> GenerateDiagnosticReportJsonAsync();
    Task<string> GenerateDiagnosticReportTextAsync();
    Task<string> ExportDiagnosticReportTextAsync(string exportPath);
}

public interface IUpdateApprovalService
{
    bool CanUpdate(string componentName);
    Task<bool> RequestUpdateApprovalAsync(UpdateProposal proposal);
}
