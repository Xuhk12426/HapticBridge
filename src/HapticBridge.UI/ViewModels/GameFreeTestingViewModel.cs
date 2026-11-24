using System.ComponentModel;
using System.Windows.Input;
using HapticBridge.Audio.Wasapi;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Integrations.VIIPER;

namespace HapticBridge.UI.ViewModels;

public sealed class GameFreeTestingViewModel : ViewModelBase
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(35);
    private static readonly ButtonLesson[] Lessons =
    [
        new(DualSenseTestButtons.Cross, "A", "× Cross", DualSenseHapticCue.Tap, DualSenseGrip.Right, "one tap"),
        new(DualSenseTestButtons.Circle, "B", "○ Circle", DualSenseHapticCue.DoubleTap, DualSenseGrip.Right, "two taps"),
        new(DualSenseTestButtons.Square, "X", "□ Square", DualSenseHapticCue.Flutter, DualSenseGrip.Right, "three quick taps"),
        new(DualSenseTestButtons.Triangle, "Y", "△ Triangle", DualSenseHapticCue.Rumble, DualSenseGrip.Right, "one strong, rising buzz"),
        new(DualSenseTestButtons.L1, "LB", "L1", DualSenseHapticCue.Bumper, DualSenseGrip.Left, "a long pulse then a tap"),
        new(DualSenseTestButtons.R1, "RB", "R1", DualSenseHapticCue.Bumper, DualSenseGrip.Right, "a long pulse then a tap"),
        new(DualSenseTestButtons.L2, "LT", "L2", null, DualSenseGrip.Left, ""),
        new(DualSenseTestButtons.R2, "RT", "R2", null, DualSenseGrip.Right, ""),
        new(DualSenseTestButtons.L3, "left stick", "L3", DualSenseHapticCue.Stick, DualSenseGrip.Left, "a tap then a thud"),
        new(DualSenseTestButtons.R3, "right stick", "R3", DualSenseHapticCue.Stick, DualSenseGrip.Right, "a tap then a thud")
    ];
    private static readonly IReadOnlyList<string> FaceLabels =
        Array.AsReadOnly(Lessons.Take(4).Select(lesson => $"{lesson.Xbox} → {lesson.PlayStation}").ToArray());
    private readonly IAudioEndpointService _audio;
    private readonly IControllerDetectionService _controllers;
    private readonly ControllerSessionViewModel? _session;
    private readonly Func<AudioEndpointModel?> _selectedOutput;
    private CancellationTokenSource? _stop;
    private string _status = string.Empty;
    private bool _isChallengeActive;
    private int _completedCues;

    internal GameFreeTestingViewModel(IAudioEndpointService audio, IControllerDetectionService controllers,
        ControllerSessionViewModel? session, Func<AudioEndpointModel?> selectedOutput)
    {
        _audio = audio;
        _controllers = controllers;
        _session = session;
        _selectedOutput = selectedOutput;
        PlaySpeakersCommand = new AsyncRelayCommand(() => PlayAsync(false), () => !IsPlaying && _selectedOutput() is not null);
        PlayDualSenseCommand = new AsyncRelayCommand(() => PlayAsync(true),
            () => !IsPlaying && _session?.HasInput == true && _selectedOutput() is not null);
        StartChallengeCommand = new AsyncRelayCommand(PlayChallengeAsync,
            () => !IsPlaying && _session?.HasInput == true);
        StopCommand = new RelayCommand(Stop, () => IsPlaying);
    }

    public bool IsPlaying => _stop is not null;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsChallengeActive { get => _isChallengeActive; private set => SetProperty(ref _isChallengeActive, value); }
    public int CompletedCues { get => _completedCues; private set => SetProperty(ref _completedCues, value); }
    public int CueCount => Lessons.Length;
    public IReadOnlyList<string> FaceButtonGuide => FaceLabels;
    public ICommand PlaySpeakersCommand { get; }
    public ICommand PlayDualSenseCommand { get; }
    public ICommand StartChallengeCommand { get; }
    public ICommand StopCommand { get; }

    internal void Stop() => _stop?.Cancel();

    private Task PlayAsync(bool dualSense) => RunTestAsync(async token =>
    {
        string? selectedId = _selectedOutput()?.Id;
        var (listening, endpoint) = await Task.Run(() =>
        {
            var output = FindListeningEndpoint(selectedId);
            return (output, dualSense ? FindDualSenseEndpoint() : output);
        }, token);
        Status = dualSense ? $"Playing on {listening.FriendlyName}. Follow the beat from left to right."
            : $"Playing two tones on {listening.FriendlyName}…";
        int frames = await WasapiTestSignal.PlayAsync(endpoint.Id, dualSense, token);
        Status = dualSense
            ? "Beat complete. The virtual DualSense received alternating left and right cues."
            : $"Windows played {frames:N0} test frames on {endpoint.FriendlyName}. You should have heard two tones.";
    });

    private Task PlayChallengeAsync() => RunTestAsync(async token =>
    {
        if (_session?.IsRunning != true) throw new InvalidOperationException("Enable DualSense haptics before starting Pulse Trail.");
        IsChallengeActive = true;
        CompletedCues = 0;
        var endpoint = await Task.Run(FindDualSenseEndpoint, token);
        foreach (var lesson in Lessons)
        {
            await PlayLessonAsync(_session, lesson, endpoint.Id, token);
            CompletedCues++;
        }
        Status = $"Pulse Trail complete. All {CueCount} inputs were received and their cues were sent.";
    });

    private async Task RunTestAsync(Func<CancellationToken, Task> test)
    {
        if (IsPlaying) return;
        using var stop = new CancellationTokenSource();
        _stop = stop;
        CommandManager.InvalidateRequerySuggested();
        Status = "Checking Windows playback devices…";
        try
        {
            await test(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Status = "Test stopped."; }
        catch (Exception exception) { Status = $"Test could not complete. {exception.Message}"; }
        finally
        {
            IsChallengeActive = false;
            _stop = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task PlayLessonAsync(ControllerSessionViewModel session, ButtonLesson lesson, string endpointId,
        CancellationToken token)
    {
        if (!session.IsRunning) throw new InvalidOperationException("The virtual DualSense disconnected.");
        string prompt = $"Step {CompletedCues + 1} of {CueCount} · Xbox {lesson.Xbox} / PlayStation {lesson.PlayStation}";
        if (lesson.Cue is not { } cue)
        {
            await PlayTriggerLessonAsync(session, lesson.Grip, endpointId, prompt, token);
            return;
        }

        var press = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var button = new ButtonPressGate(lesson.Button, session.TestInput?.Buttons);
        string action = cue == DualSenseHapticCue.Stick ? "Click" : "Press";
        Status = $"{prompt} · {action} to feel the cue.";

        session.PropertyChanged += ObserveButton;
        try
        {
            try { await press.Task.WaitAsync(PromptTimeout, token); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("The button prompt timed out. Start Pulse Trail again when the controller is ready.");
            }

            string grip = lesson.Grip == DualSenseGrip.Left ? "left" : "right";
            Status = $"{lesson.Xbox} → {lesson.PlayStation} · Feel for {lesson.Feel} in the {grip} grip.";
            await WasapiTestSignal.PlayHapticCueAsync(endpointId, cue, lesson.Grip, token);
        }
        finally { session.PropertyChanged -= ObserveButton; }

        void ObserveButton(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (nameof(ControllerSessionViewModel.IsRunning) or nameof(ControllerSessionViewModel.TestInput)))
                return;
            if (!session.IsRunning)
                press.TrySetException(new InvalidOperationException("The virtual DualSense disconnected."));
            else if (!press.Task.IsCompleted && button.Observe(session.TestInput?.Buttons))
                press.TrySetResult();
        }
    }

    private async Task PlayTriggerLessonAsync(ControllerSessionViewModel session, DualSenseGrip grip,
        string endpointId, string prompt, CancellationToken token)
    {
        byte? initial = ReadTrigger();
        var trigger = new TriggerReleaseGate(initial);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(PromptTimeout);
        bool inputLost = false;
        UpdateTriggerStatus(initial);
        session.PropertyChanged += ObserveTrigger;
        try
        {
            // Open the output while the prompt is waiting so a quick squeeze has a ready audio stream.
            await WasapiTestSignal.PlayTriggerRumbleAsync(endpointId, grip, trigger.ReadTravel, stop.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && stop.IsCancellationRequested)
        {
            throw new InvalidOperationException(inputLost
                ? "The controller connection was interrupted during the trigger test."
                : "The trigger prompt timed out. Start Pulse Trail again when the controller is ready.");
        }
        finally { session.PropertyChanged -= ObserveTrigger; }

        byte? ReadTrigger() => grip == DualSenseGrip.Left ? session.TestInput?.LeftTrigger : session.TestInput?.RightTrigger;

        void UpdateTriggerStatus(byte? position)
        {
            string instruction = position switch
            {
                null => "Waiting for controller input.",
                0 when trigger.Completed => "0% · Released.",
                0 => "0% · Squeeze, then release to continue.",
                _ when trigger.Pressed => $"{position.Value / 255.0:0.#%} · Squeeze farther for stronger rumble. Release to continue.",
                _ => $"{position.Value / 255.0:0.#%} · Release fully to begin."
            };
            Status = $"{prompt} · {instruction}";
        }

        void ObserveTrigger(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (nameof(ControllerSessionViewModel.IsRunning) or nameof(ControllerSessionViewModel.TestInput)))
                return;
            if (stop.IsCancellationRequested) return;
            byte? position = ReadTrigger();
            if (!session.IsRunning || (position is null && trigger.Pressed && !trigger.Completed))
            {
                inputLost = true;
                stop.Cancel();
                return;
            }
            if (trigger.Completed) return;

            bool wasPressed = trigger.Pressed;
            trigger.Observe(position);
            if (!wasPressed && trigger.Pressed) stop.CancelAfter(Timeout.InfiniteTimeSpan);
            UpdateTriggerStatus(position);
        }
    }

    private AudioEndpointModel FindDualSenseEndpoint()
    {
        if (!_controllers.DetectControllers().IsVirtualDualSenseDetected)
            throw new InvalidOperationException("Wait for the virtual DualSense to connect, then try again.");
        var devices = _audio.GetAllRenderEndpoints()
            .Where(endpoint => endpoint.IsActive && endpoint.EndpointType == AudioEndpointType.VirtualDualSense).ToArray();
        if (devices.Length != 1)
            throw new InvalidOperationException("The test needs exactly one active DualSense audio output.");
        return devices[0];
    }

    private AudioEndpointModel FindListeningEndpoint(string? selectedId)
    {
        var selected = _audio.GetPhysicalPlaybackEndpoints().FirstOrDefault(endpoint =>
            string.Equals(endpoint.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (selected is null) throw new InvalidOperationException("Choose an active listening output.");
        if (selected.IsMuted == true || selected.VolumeLevel <= 0)
            throw new InvalidOperationException("Raise Listening volume above 0% before playing the audio test.");
        return selected;
    }

    private sealed record ButtonLesson(DualSenseTestButtons Button, string Xbox, string PlayStation,
        DualSenseHapticCue? Cue, DualSenseGrip Grip, string Feel);
}

internal sealed class ButtonPressGate(DualSenseTestButtons target, DualSenseTestButtons? initial)
{
    private bool _released = initial == DualSenseTestButtons.None;

    public bool Observe(DualSenseTestButtons? buttons)
    {
        bool correct = _released && buttons == target;
        _released = buttons == DualSenseTestButtons.None;
        return correct;
    }
}

internal sealed class TriggerReleaseGate(byte? initial)
{
    private bool _sawNeutral = initial == 0;
    private readonly object _audioLock = new();
    private byte? _travel = 0;
    private byte? _pendingTravel;
    public bool Pressed { get; private set; }
    public bool Completed { get; private set; }

    public void Observe(byte? position)
    {
        if (Completed) return;
        switch (position)
        {
            case null:
                _sawNeutral = false;
                Pressed = false;
                break;
            case 0:
                _sawNeutral = true;
                Completed = Pressed;
                break;
            default:
                Pressed = _sawNeutral;
                break;
        }
        lock (_audioLock)
        {
            _travel = Completed ? null : Pressed ? position : (byte)0;
            if (position is null)
                _pendingTravel = null;
            else if (_travel > _pendingTravel.GetValueOrDefault())
                _pendingTravel = _travel;
        }
    }

    public byte? ReadTravel()
    {
        lock (_audioLock)
        {
            // Preserve a squeeze that was released between two audio buffers.
            byte? next = _travel ?? _pendingTravel;
            _pendingTravel = null;
            return next;
        }
    }
}
