using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Loads stacks/{stackId}/docker-stack.template.yml(a), deserializes into the typed compose model,
    /// assigns it to ctx.Template, and initializes ctx.Working as an independent clone (by re-deserializing).
    /// </summary>
    public sealed class LoadTemplateStage : IRenderStage
    {
        public int Order => 100;

        private readonly IFileSystem _fs;
        private readonly IYamlEngine _yaml;

        public LoadTemplateStage(IFileSystem fs, IYamlEngine yaml)
        {
            _fs = fs;
            _yaml = yaml;
        }

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            var stackId = ctx.Request.StackId;

            // Resolve template path under stacks/{stackId}
            var baseDir  = Path.Combine(ctx.StacksDir, stackId);
            var ymlPath  = Path.Combine(baseDir, "docker-stack.template.yml");
            var yamlPath = Path.Combine(baseDir, "docker-stack.template.yaml");

            var templatePath = _fs.FileExists(ymlPath) ? ymlPath
                             : _fs.FileExists(yamlPath) ? yamlPath
                             : throw new FileNotFoundException(
                                   $"Template not found. Expected: {ymlPath} or {yamlPath}");

            ctx.Template = await _yaml.LoadYamlAsync<ComposeFile>(templatePath, ct);
            ctx.Working = await _yaml.LoadYamlAsync<ComposeFile>(templatePath, ct);
            ctx.TemplatePath = templatePath;
        }
    }
}