namespace SwarmBender.Services.Models;

/// <summary>Severity of a validation issue.</summary>
public enum ValidationSeverity { Error, Warning }

/// <summary>One validation issue entry.</summary>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? File = null,
    string? Path = null);
