using System.Diagnostics;
using System.Windows.Input;
using HapticBridge.Setup.Engine;

namespace HapticBridge.UI.ViewModels;

public sealed class SetupRepairViewModel : ViewModelBase
{
    private readonly SetupRepairEngine _engine;
    private readonly bool _externalToolsOnly;
    private SetupRepairPlan? _plan;
    private IReadOnlyList<SetupRepairItem> _items = [];
    private string _summary = "Checking Windows setup…";
    private bool _isBusy;
    private bool _isApplying;

    public SetupRepairViewModel(SetupRepairEngine engine, bool externalToolsOnly = false)
    {
        _engine = engine;
        _externalToolsOnly = externalToolsOnly;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && _plan is { ChangeCount: > 0 });
        RepairHidHideCommand = new AsyncRelayCommand(() => ApplyAsync(onlyHidHide: true),
            () => !IsBusy && _plan is { HasHidHideRepair: true });
    }

    public ICommand CheckCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RepairHidHideCommand { get; }
    public IReadOnlyList<SetupRepairItem> Items { get => _items; private set => SetProperty(ref _items, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool IsApplying { get => _isApplying; private set => SetProperty(ref _isApplying, value); }

    internal async Task CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _plan = null;
        Summary = "Checking Windows setup…";
        try
        {
            var plan = await Task.Run(() => _engine.Inspect(includeHidHide: !_externalToolsOnly));
            _plan = plan;
            Items = plan.Items;
            Summary = plan.ChangeCount switch
            {
                1 => "1 repair available",
                > 1 => $"{plan.ChangeCount} repairs available",
                _ when Items.Any(item => item.State == SetupRepairState.NeedsAttention) => "Some items need attention",
                _ => "Windows setup checks passed"
            };
        }
        catch (Exception exception)
        {
            Items = [];
            Trace.TraceWarning("Setup inspection failed: {0}", exception);
            Summary = "Windows setup could not be checked. Try checking again.";
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    internal Task ApplyAsync() => ApplyAsync(onlyHidHide: false);

    private async Task ApplyAsync(bool onlyHidHide)
    {
        if (IsBusy || _plan is not { ChangeCount: > 0 } plan || (onlyHidHide && !plan.HasHidHideRepair)) return;
        _plan = null;
        IsApplying = true;
        IsBusy = true;
        Summary = "Applying repairs…";
        CommandManager.InvalidateRequerySuggested();
        var progress = new Progress<string>(component =>
        {
            if (IsApplying) Summary = $"Applying {component} repair…";
        });

        try
        {
            Items = await Task.Run(() => _engine.Apply(plan, progress, onlyHidHide));
            if (onlyHidHide)
            {
                var updatedItems = Items.ToDictionary(item => item.Component);
                Items = plan.Items.Select(item => updatedItems.GetValueOrDefault(item.Component, item)).ToArray();
            }
            int verified = Items.Count(item => item.State == SetupRepairState.Verified);
            bool needsAttention = Items.Any(item => item.State is SetupRepairState.NeedsAttention or SetupRepairState.Unverified);
            Summary = verified == 0 ? "No new repairs were verified" :
                needsAttention ? $"Verified repairs: {verified} · Some items need attention" : $"Verified repairs: {verified}";
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Setup repair run failed: {0}", exception);
            Summary = "Repairs could not finish. Check again to inspect the current settings.";
        }
        finally
        {
            IsApplying = false;
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
