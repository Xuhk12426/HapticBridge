using System.IO;
using System.Windows.Input;
using HapticBridge.Core.Services;

namespace HapticBridge.UI.ViewModels;

public class DiagnosticsViewModel : ViewModelBase
{
    private readonly IDiagnosticsService _diagnostics;
    private string _reportContent = "Generating diagnostic report...";
    private string _exportStatusMessage = string.Empty;
    private bool _isBusy;
    private bool _hasReport;

    public string ReportContent
    {
        get => _reportContent;
        set => SetProperty(ref _reportContent, value);
    }

    public string ExportStatusMessage
    {
        get => _exportStatusMessage;
        set => SetProperty(ref _exportStatusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanSave));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanSave => _hasReport && !IsBusy;
    public ICommand RefreshReportCommand { get; }

    public DiagnosticsViewModel(IDiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;

        RefreshReportCommand = new AsyncRelayCommand(LoadReportAsync, () => !IsBusy);
    }

    public async Task LoadReportAsync()
    {
        if (IsBusy) return;
        _hasReport = false;
        IsBusy = true;
        ExportStatusMessage = "Checking devices…";
        try
        {
            var text = await Task.Run(_diagnostics.GenerateDiagnosticReportTextAsync);

            ReportContent = text;
            _hasReport = true;
            ExportStatusMessage = "Diagnostic report generated.";
        }
        catch (Exception ex)
        {
            ReportContent = ex.Message;
            ExportStatusMessage = "Could not inspect this PC. Select Refresh to try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveReportAsync(string target)
    {
        if (!CanSave) return;
        IsBusy = true;
        try
        {
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!,
                $".hapticbridge-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, ReportContent);
                File.Move(temporary, target, overwrite: true);
            }
            finally { File.Delete(temporary); }
            ExportStatusMessage = $"Saved to {target}";
        }
        catch (Exception ex)
        {
            ExportStatusMessage = "Export failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
