using Microsoft.Extensions.Options;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Governance;

public sealed class ApprovalAuthority(IOptions<GovernanceOptions> options) : IApprovalAuthority
{
    public ApprovalCheck CanApprove(WorkflowRun run, ApprovalRequest request, string approver)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(approver))
        {
            return new ApprovalCheck(false, "Approver identity is required.");
        }

        var settings = options.Value;
        var registered = settings.Approvers.FirstOrDefault(a => a.Name.Equals(approver, StringComparison.OrdinalIgnoreCase));
        if (registered is null)
        {
            return new ApprovalCheck(false, $"'{approver}' is not a registered approver.");
        }

        if (settings.RequireSegregationOfDuties && approver.Equals(run.InitiatedBy, StringComparison.OrdinalIgnoreCase))
        {
            return new ApprovalCheck(false, "Segregation of duties: the run initiator cannot approve their own change.");
        }

        if (request.Risk > registered.MaxRisk)
        {
            return new ApprovalCheck(false, $"{registered.Role} '{approver}' may approve up to {registered.MaxRisk} risk; this request is {request.Risk}.");
        }

        return new ApprovalCheck(true, $"Authorised as {registered.Role}.");
    }
}
