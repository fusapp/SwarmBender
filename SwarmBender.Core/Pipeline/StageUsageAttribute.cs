namespace SwarmBender.Core.Pipeline;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StageUsageAttribute : Attribute
{
    public PipelineMode[] Modes { get; }
    public StageUsageAttribute(params PipelineMode[] modes) => Modes = modes ?? Array.Empty<PipelineMode>();
}