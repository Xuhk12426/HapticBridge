using System.Windows.Input;
using HapticBridge.Core.Models;

namespace HapticBridge.UI.ViewModels;

public class UpdateConfirmationViewModel : ViewModelBase
{
    private readonly UpdateProposal _proposal;

    public string ApplicationName => _proposal.ApplicationName;
    public string InstalledVersion => _proposal.InstalledVersion;
    public string ProposedVersion => _proposal.ProposedVersion;
    public string OfficialSourceUrl => _proposal.OfficialSourceUrl;
    public string RecommendationReason => _proposal.RecommendationReason;
    public string RequirementType => _proposal.IsRequired ? "Required" : "Optional (Recommended)";
    public string CompatibilityNotes => _proposal.CompatibilityNotes;
    public string RestartNecessity => _proposal.RequiresRestart ? $"Restart required ({_proposal.RestartTarget})" : "No restart required";
    public string BackupStatus => "Configuration will be automatically backed up prior to any approved update.";

    public string ConfirmationQuestion => "Would you like to update this application?";

    public bool UserApproved { get; private set; }

    public ICommand ApproveUpdateCommand { get; }
    public ICommand DeclineUpdateCommand { get; }

    public event Action? RequestClose;

    public UpdateConfirmationViewModel(UpdateProposal proposal)
    {
        _proposal = proposal;

        ApproveUpdateCommand = new RelayCommand(() =>
        {
            UserApproved = true;
            RequestClose?.Invoke();
        });

        DeclineUpdateCommand = new RelayCommand(() =>
        {
            UserApproved = false;
            RequestClose?.Invoke();
        });
    }
}
