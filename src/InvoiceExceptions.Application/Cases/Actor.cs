namespace InvoiceExceptions.Application.Cases;

/// <summary>
/// Who is changing a case and through which door. The channel is set by the entry point (never by the caller's
/// payload), which is what makes "only a human may confirm" enforceable.
/// </summary>
public sealed record Actor(string Channel, string Kind, string? Name)
{
    /// <summary>The acceptance runner: replays prepared inputs, including the human confirmations of its fixtures.</summary>
    public static Actor Runner { get; } = new("cli", "runner", null);

    /// <summary>The agent connected over MCP.</summary>
    public static Actor Agent { get; } = new("mcp", "agent", null);

    /// <summary>A person using the case page.</summary>
    public static Actor Human(string name) => new("web", "human", name);

    public bool IsAgent => Kind == "agent";
}

/// <summary>Side information about a change that is not part of the step-input contract.</summary>
/// <param name="DocumentSourcePath">Where the submitted document's PDF was read from.</param>
/// <param name="ProposalRationales">The agent's explanation per proposal id, shown to the human who has to confirm.</param>
public sealed record ChangeNotes(
    string? DocumentSourcePath = null,
    IReadOnlyDictionary<string, string>? ProposalRationales = null);
