namespace SwarmBender.Core.Pipeline;

public enum PipelineMode
{
    StackRender,    // klasik: SerializeStage ile .stack.yml üret
    ConfigExport    // appsettings.json çıkar
}