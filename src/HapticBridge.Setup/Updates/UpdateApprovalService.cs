using HapticBridge.Core.Models;
using HapticBridge.Core.Services;

namespace HapticBridge.Setup.Updates;

public class UpdateApprovalService : IUpdateApprovalService
{
    // Record of user decisions: ComponentName -> Approved / Declined
    private readonly Dictionary<string, bool> _userDecisions = new(StringComparer.OrdinalIgnoreCase);

    public bool CanUpdate(string componentName)
    {
        // Under no circumstances can an update proceed without explicit runtime user approval!
        if (_userDecisions.TryGetValue(componentName, out bool approved))
        {
            return approved;
        }
        return false;
    }

    public async Task<bool> RequestUpdateApprovalAsync(UpdateProposal proposal)
    {
        // Must never auto-approve.
        // In GUI mode, this triggers the mandatory Update Confirmation Screen from Section 2.3.
        return await Task.FromResult(false);
    }

    public void RegisterUserDecision(string componentName, bool approved)
    {
        _userDecisions[componentName] = approved;
    }

    public static UpdateProposal CreateProposal(string componentName, string currentVersion, string newVersion, string sourceUrl, string reason, bool required = false)
    {
        return new UpdateProposal
        {
            ApplicationName = componentName,
            InstalledVersion = currentVersion,
            ProposedVersion = newVersion,
            OfficialSourceUrl = sourceUrl,
            RecommendationReason = reason,
            IsRequired = required,
            CompatibilityNotes = "Preserving existing working configuration takes priority over updates.",
            RequiresAdmin = true,
            RequiresRestart = true,
            RestartTarget = "Application / Service"
        };
    }
}
