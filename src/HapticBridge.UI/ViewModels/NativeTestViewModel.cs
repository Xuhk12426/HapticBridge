using System.Windows.Input;
using HapticBridge.Audio.Wasapi;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Integrations.SoundSync;

namespace HapticBridge.UI.ViewModels;

public sealed class NativeTestViewModel : ViewModelBase
{
    private readonly IAudioEndpointService _audio;
    private readonly Func<bool> _soundSyncRunning;
    private readonly WasapiLoopbackRoute _audioRoute = new();
    private CancellationTokenSource? _audioStop;
    private IReadOnlyList<AudioEndpointModel> _sources = [];
    private IReadOnlyList<AudioEndpointModel> _cables = [];
    private AudioEndpointModel? _source;
    private AudioEndpointModel? _cable;
    private bool _checking;
    private string _audioStatus = "Refresh endpoints after the DualSense audio device appears.";

    public NativeTestViewModel(IAudioEndpointService audio, SoundSyncIntegration soundSync, ControllerSessionViewModel controller)
        : this(audio, () => soundSync.IsRunning, controller) { }

    internal NativeTestViewModel(IAudioEndpointService audio, Func<bool> soundSyncRunning, ControllerSessionViewModel controller)
    {
        _audio = audio;
        _soundSyncRunning = soundSyncRunning;
        Controller = controller;
        RefreshEndpointsCommand = new AsyncRelayCommand(RefreshEndpointsAsync, () => !AudioRunning && !_checking);
        StartAudioCommand = new AsyncRelayCommand(StartAudioAsync, () => !AudioRunning && !_checking && Source is not null && Cable is not null);
        StopAudioCommand = new RelayCommand(StopAudio, () => AudioRunning);
    }

    public IReadOnlyList<AudioEndpointModel> Sources { get => _sources; private set => SetProperty(ref _sources, value); }
    public IReadOnlyList<AudioEndpointModel> Cables { get => _cables; private set => SetProperty(ref _cables, value); }
    public AudioEndpointModel? Source
    {
        get => _source;
        set { if (SetProperty(ref _source, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    public AudioEndpointModel? Cable
    {
        get => _cable;
        set { if (SetProperty(ref _cable, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    public bool AudioRunning => _audioStop is not null;
    public string AudioStatus { get => _audioStatus; private set => SetProperty(ref _audioStatus, value); }
    public ControllerSessionViewModel Controller { get; }
    public ICommand RefreshEndpointsCommand { get; }
    public ICommand StartAudioCommand { get; }
    public ICommand StopAudioCommand { get; }

    internal async Task RefreshEndpointsAsync()
    {
        if (_checking || AudioRunning) return;
        _checking = true;
        AudioStatus = "Checking active playback endpoints…";
        try
        {
            string? sourceId = Source?.Id;
            string? cableId = Cable?.Id;
            var endpoints = await Task.Run(_audio.GetAllRenderEndpoints);
            Sources = endpoints.Where(e => e.IsActive && e.EndpointType == AudioEndpointType.VirtualDualSense).ToArray();
            Cables = endpoints.Where(e => e.IsActive && e.EndpointType == AudioEndpointType.VirtualCable).ToArray();
            Source = Sources.FirstOrDefault(e => e.Id == sourceId) ?? (Sources.Count == 1 ? Sources[0] : null);
            var preferred = Cables.Where(e => e.FriendlyName.StartsWith("CABLE In 16 Ch (", StringComparison.OrdinalIgnoreCase)).ToArray();
            Cable = Cables.FirstOrDefault(e => e.Id == cableId) ??
                (Cables.Count == 1 ? Cables[0] : preferred.Length == 1 ? preferred[0] : null);
            AudioStatus = Sources.Count == 0
                ? "Waiting for a DualSense playback endpoint. Refresh after VIIPER attaches the device."
                : "Select the DualSense and VB-CABLE endpoints, then start the audio test.";
        }
        catch (Exception exception) { AudioStatus = exception.Message; }
        finally
        {
            _checking = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal async Task StartAudioAsync()
    {
        if (_checking || AudioRunning || Source is not { } source || Cable is not { } cable) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _audioStop = stop;
        RunningChanged(nameof(AudioRunning));
        AudioStatus = "Checking the four-channel route before opening audio streams…";
        var token = stop.Token;
        try
        {
            if (await Task.Run(_soundSyncRunning, token))
                throw new InvalidOperationException("Close SoundSync before starting the native audio test to avoid two routes into VB-CABLE.");
            var probe = await Task.Run(() => _audioRoute.Probe(source, cable), token);
            if (probe.DestinationChannels.Length != 4)
            {
                AudioStatus = probe.Detail;
                return;
            }
            AudioStatus = $"Running for up to 60 seconds. VB-CABLE channels {string.Join(", ", probe.DestinationChannels)}. Play the game's haptic test now.";
            var result = await _audioRoute.RunAsync(source, cable, token);
            AudioStatus = $"Audio test stopped. Captured {result.CapturedFrames:N0} frames, forwarded {result.ForwardedFrames:N0}, " +
                $"dropped {result.DroppedFrames:N0}, underrun {result.UnderrunFrames:N0}, discontinuities {result.Discontinuities:N0}.";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { AudioStatus = "Audio test stopped."; }
        catch (Exception exception) { AudioStatus = $"Audio test could not run. {exception.Message}"; }
        finally
        {
            _audioStop = null;
            RunningChanged(nameof(AudioRunning));
        }
    }

    public void StopAudio() => _audioStop?.Cancel();

    private void RunningChanged(string property)
    {
        OnPropertyChanged(property);
        CommandManager.InvalidateRequerySuggested();
    }

}
