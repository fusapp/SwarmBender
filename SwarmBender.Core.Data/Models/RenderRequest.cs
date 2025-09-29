namespace SwarmBender.Core.Data.Models;

public sealed record RenderRequest(
    string RootPath,
    string StackId,
    string Env,
    string AppSettingsMode,   // "env" | "config" (kept for future stages)
    string OutDir,
    bool   WriteHistory
)
{
   
}