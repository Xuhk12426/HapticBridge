using System.Windows.Input;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.VIIPER;
using HapticBridge.Setup.Engine;

namespace HapticBridge.UI.ViewModels;

public sealed class ControllerSessionViewModel : ViewModelBase
{
    private readonly Func<bool> _apolloRunning;
    private readonly Func<bool> _ds4WindowsRunning;
    private readonly Func<IProgress<ViiperTestUpdate>, CancellationToken, IProgress<DualSenseTestInput?>, Task<ViiperTestUpdate>> _run;
    private readonly Func<SetupRepairItem?>? _prepareAccess;
    private readonly Func<CancellationToken, Task>? _maximizeHaptics;
    private CancellationTokenSource? _stop;
    private Task? _session;
    private string _status = "Start a stream in Artemis, then enable haptics here to connect the virtual DualSense.";
    private DualSenseTestInput? _testInput;

    public ControllerSessionViewModel(ApolloIntegration apollo, DS4WindowsIntegration ds4Windows,
        ViiperSpeakerPcmHandler? speakerPcm = null, Func<SetupRepairItem?>? prepareAccess = null,
        Func<CancellationToken, Task>? maximizeHaptics = null)
        : this(() => apollo.IsRunning, () => ds4Windows.IsRunning, prepareAccess,
            new ViiperControllerTest(speakerPcm).RunAsync) => _maximizeHaptics = maximizeHaptics;

    internal ControllerSessionViewModel(Func<bool> apolloRunning, Func<bool> ds4WindowsRunning,
        Func<IProgress<ViiperTestUpdate>, CancellationToken, Task<ViiperTestUpdate>> run,
        Func<SetupRepairItem?>? prepareAccess = null)
        : this(apolloRunning, ds4WindowsRunning, prepareAccess, (progress, stop, _) => run(progress, stop)) { }

    internal ControllerSessionViewModel(Func<bool> apolloRunning, Func<bool> ds4WindowsRunning,
        Func<SetupRepairItem?>? prepareAccess,
        Func<IProgress<ViiperTestUpdate>, CancellationToken, IProgress<DualSenseTestInput?>, Task<ViiperTestUpdate>> run)
    {
        _apolloRunning = apolloRunning;
        _ds4WindowsRunning = ds4WindowsRunning;
        _run = run;
        _prepareAccess = prepareAccess;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning);
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public event Action<string>? UnexpectedDisconnect;
    public bool IsRunning => _stop is not null;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasInput => TestInput.HasValue;
    public string ConnectionTitle => HasInput ? "Controller connected" :
        IsRunning ? "Connecting your controller" : "Connect your controller";
    public DualSenseTestInput? TestInput
    {
        get => _testInput;
        private set
        {
            bool hadInput = HasInput;
            SetProperty(ref _testInput, value);
            if (hadInput == HasInput) return;
            OnPropertyChanged(nameof(HasInput));
            OnPropertyChanged(nameof(ConnectionTitle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal Task StartAsync()
    {
        if (IsRunning) return _session ?? Task.CompletedTask;
        var stop = new CancellationTokenSource();
        _stop = stop;
        RunningChanged();
        return _session = RunAsync(stop);
    }

    public async Task StopAsync()
    {
        _stop?.Cancel();
        if (_session is { } session) await session;
    }

    private async Task RunAsync(CancellationTokenSource stop)
    {
        Status = "Checking Apollo input and VIIPER…";
        var token = stop.Token;
        try
        {
            var (apollo, ds4) = await Task.Run(() => (_apolloRunning(), _ds4WindowsRunning()), token);
            if (!apollo) throw new InvalidOperationException("Start Apollo and connect the Artemis controller first.");
            if (ds4) throw new InvalidOperationException("Close DS4Windows before enabling haptics here.");
            if (_prepareAccess is not null)
            {
                Status = "Checking HidHide access…";
                var access = await Task.Run(_prepareAccess, token);
                if (access?.State is SetupRepairState.NeedsAttention or SetupRepairState.Unverified)
                    throw new InvalidOperationException(access.Detail);
            }
            token.ThrowIfCancellationRequested();
            var progress = new Progress<ViiperTestUpdate>(update =>
            {
                if (ReferenceEquals(_stop, stop) && !stop.IsCancellationRequested) Status = Format(update);
            });
            var testInput = new Progress<DualSenseTestInput?>(input =>
            {
                if (ReferenceEquals(_stop, stop) && !stop.IsCancellationRequested) TestInput = input;
            });
            using var volumeStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task? volume = _maximizeHaptics?.Invoke(volumeStop.Token);
            try { Status = Format(await _run(progress, token, testInput)); }
            finally
            {
                volumeStop.Cancel();
                if (volume is not null)
                {
                    try { await volume; }
                    catch (OperationCanceledException) when (volumeStop.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Trace.TraceWarning("Could not set the haptics endpoint volume: {0}", exception);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Status = "Virtual DualSense disconnected."; }
        catch (Exception exception)
        {
            Status = $"Virtual DualSense session ended. {exception.Message}";
            if (!stop.IsCancellationRequested && exception is ViiperControllerDisconnectedException)
                UnexpectedDisconnect?.Invoke(exception.Message);
        }
        finally
        {
            _stop = null;
            TestInput = null;
            stop.Dispose();
            RunningChanged();
        }
    }

    private void RunningChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ConnectionTitle));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Format(ViiperTestUpdate update) => update.InputReports == 0 && update.FeedbackFrames == 0
        ? update.Detail
        : $"{update.Detail} Input reports sent {update.InputReports:N0}. V5 feedback frames {update.FeedbackFrames:N0}.";
}
