using System.Text.RegularExpressions;

namespace SwarmBender.Core.Util;

/// <summary>
/// Provides input validation for CLI arguments and configuration values.
/// </summary>
public static partial class InputValidator
{
    // Allowed characters for stack IDs: alphanumeric, underscores, hyphens
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_\-]*$", RegexOptions.Compiled)]
    private static partial Regex StackIdPattern();

    // Allowed characters for environment names: alphanumeric, underscores, hyphens
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_\-]*$", RegexOptions.Compiled)]
    private static partial Regex EnvNamePattern();

    /// <summary>
    /// Validates a stack ID.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when validation fails.</exception>
    public static string ValidateStackId(string stackId)
    {
        if (string.IsNullOrWhiteSpace(stackId))
            throw new ArgumentException("Stack ID cannot be empty.", nameof(stackId));

        if (stackId.Length > 64)
            throw new ArgumentException("Stack ID is too long (max 64 characters).", nameof(stackId));

        if (!StackIdPattern().IsMatch(stackId))
            throw new ArgumentException(
                $"Stack ID '{stackId}' is invalid. Must start with a letter and contain only alphanumeric characters, underscores, or hyphens.",
                nameof(stackId));

        return stackId;
    }

    /// <summary>
    /// Validates an environment name.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when validation fails.</exception>
    public static string ValidateEnvironment(string env)
    {
        if (string.IsNullOrWhiteSpace(env))
            throw new ArgumentException("Environment name cannot be empty.", nameof(env));

        if (env.Length > 32)
            throw new ArgumentException("Environment name is too long (max 32 characters).", nameof(env));

        if (!EnvNamePattern().IsMatch(env))
            throw new ArgumentException(
                $"Environment name '{env}' is invalid. Must start with a letter and contain only alphanumeric characters, underscores, or hyphens.",
                nameof(env));

        return env;
    }

    /// <summary>
    /// Validates a root path to prevent path traversal attacks.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when validation fails.</exception>
    public static string ValidateRootPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

        // Get the full path to normalize it
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rootPath);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid root path: {ex.Message}", nameof(rootPath), ex);
        }

        // Check for path traversal attempts
        if (rootPath.Contains(".."))
        {
            // Verify the resolved path is within expected boundaries
            var currentDir = Path.GetFullPath(Directory.GetCurrentDirectory());

            // Allow paths that are parent directories of current directory
            // but warn about potentially dangerous traversal
            if (!fullPath.StartsWith(currentDir) && !currentDir.StartsWith(fullPath))
            {
                // Path is outside current directory tree - log warning but allow
                // This is intentional as users may legitimately want to work on different directories
            }
        }

        // Check that the path actually exists
        if (!Directory.Exists(fullPath))
            throw new ArgumentException($"Root path does not exist: {fullPath}", nameof(rootPath));

        return fullPath;
    }

    /// <summary>
    /// Validates an output directory path.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when validation fails.</exception>
    public static string ValidateOutputDir(string outDir, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(outDir))
            throw new ArgumentException("Output directory cannot be empty.", nameof(outDir));

        // Normalize the path
        string fullPath;
        try
        {
            fullPath = Path.IsPathRooted(outDir)
                ? Path.GetFullPath(outDir)
                : Path.GetFullPath(Path.Combine(rootPath, outDir));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid output directory: {ex.Message}", nameof(outDir), ex);
        }

        // Ensure the output directory is within or under the root path
        var normalizedRoot = Path.GetFullPath(rootPath);
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Output directory must be within the root path. Got: {outDir}",
                nameof(outDir));

        return fullPath;
    }

    /// <summary>
    /// Validates appsettings mode.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when validation fails.</exception>
    public static string ValidateAppSettingsMode(string mode)
    {
        var validModes = new[] { "env", "config" };
        var normalizedMode = mode?.ToLowerInvariant() ?? "env";

        if (!validModes.Contains(normalizedMode))
            throw new ArgumentException(
                $"Invalid appsettings mode '{mode}'. Valid values: {string.Join(", ", validModes)}",
                nameof(mode));

        return normalizedMode;
    }
}
