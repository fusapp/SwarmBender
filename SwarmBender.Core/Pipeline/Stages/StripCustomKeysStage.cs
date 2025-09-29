using System;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Removes any non-compose extension keys so the final YAML is Swarm-safe:
    /// - Clears Service.X_Sb_Groups
    /// - Removes any "x-sb" entry from ComposeNode.Custom at root and services
    /// </summary>
    [StageUsage(PipelineMode.ConfigExport, PipelineMode.StackRender)]
    public sealed class StripCustomKeysStage : IRenderStage
    {
        public int Order => 790; // after TokenExpand(700), before Serialize(800)

        public Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException("Working model is null.");

            // root-level custom cleanup
            ctx.Working.Custom?.Remove("x-sb");

            if (ctx.Working.Services is not null)
            {
                foreach (var (_, svc) in ctx.Working.Services)
                {
                    ct.ThrowIfCancellationRequested();

                    // typed x-sb props
                    if (svc.X_Sb_Groups is not null) svc.X_Sb_Groups = null;
                    if (svc.X_Sb_Secrets is not null) svc.X_Sb_Secrets = null;

                    // any custom "x-sb"
                    svc.Custom?.Remove("x-sb");
                }
            }

            return Task.CompletedTask;
        }
    }
}