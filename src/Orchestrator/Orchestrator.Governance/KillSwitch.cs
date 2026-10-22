using Orchestrator.Core.Abstractions;

namespace Orchestrator.Governance;

public sealed class KillSwitch : IKillSwitch
{
    private volatile string? _reason;

    public bool IsEngaged => _reason is not null;

    public string? Reason => _reason;

    public void Engage(string reason) => _reason = string.IsNullOrWhiteSpace(reason) ? "Kill switch engaged" : reason;

    public void Release() => _reason = null;
}
