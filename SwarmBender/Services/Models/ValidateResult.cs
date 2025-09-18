namespace SwarmBender.Services.Models;

/// <summary>Combined validation result for all evaluated stacks.</summary>
public sealed record ValidateResult(IReadOnlyList<StackValidationResult> Stacks);

/// <summary>Per-stack validation result.</summary>
public sealed record StackValidationResult(
    string StackId,
    IReadOnlyList<ValidationIssue> Errors,
    IReadOnlyList<ValidationIssue> Warnings);
