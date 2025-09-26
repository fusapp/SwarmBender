using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Compose;   // ComposeFile (root model)
using SwarmBender.Core.Util;           // DeepMerge

namespace SwarmBender.Core.Pipeline.Stages
{
    /// <summary>
    /// Applies overlays using contract-configured order (SbConfig.Render.OverlayOrder),
    /// or falls back to defaults if not provided. Files are applied in ascending
    /// alphabetical order per glob; last applied wins on collisions.
    /// </summary>
    public sealed class ApplyOverlaysStage : IRenderStage
    {
        public int Order => 200;

        private readonly IFileSystem _fs;
        private readonly IYamlEngine _yaml;

        public ApplyOverlaysStage(IFileSystem fs, IYamlEngine yaml)
        {
            _fs = fs;
            _yaml = yaml;
        }

        public async Task ExecuteAsync(RenderContext ctx, CancellationToken ct)
        {
            if (ctx.Working is null)
                throw new InvalidOperationException("Working model is null. LoadTemplateStage must run before ApplyOverlaysStage.");

            var stackId = ctx.Request.StackId;
            var env     = ctx.Request.Env;

            // 1) Get overlay order from config; fallback to defaults
            var patterns = (ctx.Config?.Render?.OverlayOrder is { Count: > 0 } configured)
                ? configured
                : new List<string>
                {
                    "stacks/all/{env}/stack/*.y?(a)ml",
                    "stacks/{stackId}/{env}/stack/*.y?(a)ml"
                };

            // 2) Resolve placeholders and collect files in order
            var allFilesOrdered = new List<string>();
            foreach (var patt in patterns)
            {
                var resolved = patt
                    .Replace("{stackId}", stackId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{env}",     env,     StringComparison.OrdinalIgnoreCase);

                foreach (var f in ExpandFiles(ctx.RootPath, resolved))
                    allFilesOrdered.Add(f);
            }

            // 3) Apply overlays in order (last wins)
            foreach (var file in allFilesOrdered)
            {
                ct.ThrowIfCancellationRequested();

                var overlay = await _yaml.LoadYamlAsync<ComposeFile>(file, ct).ConfigureAwait(false);
                ShallowTopLevelMerge(ctx.Working, overlay);
            }
        }

        /// <summary>
        /// Expands a glob pattern relative to the given root using IFileSystem.GlobFiles.
        /// Supports *.y?(a)ml by trying both yml and yaml masks while preserving
        /// the original order per pattern.
        /// </summary>
        private IEnumerable<string> ExpandFiles(string root, string pattern)
        {
            var absPattern = Path.IsPathRooted(pattern) ? pattern : Path.Combine(root, pattern);

            // Support for "y?(a)ml" -> try yml and yaml variants
            if (pattern.Contains("y?(a)ml", StringComparison.OrdinalIgnoreCase))
            {
                var pYml  = absPattern.Replace("y?(a)ml", "yml",  StringComparison.OrdinalIgnoreCase);
                var pYaml = absPattern.Replace("y?(a)ml", "yaml", StringComparison.OrdinalIgnoreCase);

                var list = new List<string>();
                list.AddRange(_fs.GlobFiles(root, MakeRelativeToRoot(root, pYml)));
                list.AddRange(_fs.GlobFiles(root, MakeRelativeToRoot(root, pYaml)));

                return list.Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
            }

            // Otherwise pass-through
            return _fs.GlobFiles(root, MakeRelativeToRoot(root, absPattern))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        }

        private static string MakeRelativeToRoot(string root, string absOrRel)
        {
            // IFileSystem.GlobFiles(root, pattern) expects a pattern relative to root;
            // if absolute came in and starts with root, trim the prefix.
            if (Path.IsPathRooted(absOrRel))
            {
                var normRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normPath = Path.GetFullPath(absOrRel);
                if (normPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = normPath.Substring(normRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return rel;
                }
            }
            return absOrRel;
        }

        /// <summary>
        /// Shallow top-level merge for typed ComposeFile:
        /// - IDictionary&lt;string, T&gt; props: add/overwrite keys from overlay
        /// - IList props: append overlay items
        /// - Scalar/complex refs: overwrite if overlay value != null
        /// </summary>
        private static void ShallowTopLevelMerge(ComposeFile target, ComposeFile overlay)
        {
            if (overlay is null) return;

            var tType = typeof(ComposeFile);
            foreach (var prop in tType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;

                var oVal = prop.GetValue(overlay);
                if (oVal is null) continue;

                var tVal = prop.GetValue(target);

                // IDictionary<string, ?>
                if (IsStringKeyDictionary(prop.PropertyType, out var dictTypes))
                {
                    // Ensure target dictionary exists
                    if (tVal is null)
                    {
                        prop.SetValue(target, oVal);
                        continue;
                    }

                    var tDict = (IDictionary)tVal;
                    var oDict = (IDictionary)oVal;
                    foreach (DictionaryEntry entry in oDict)
                    {
                        tDict[entry.Key] = entry.Value; // last-wins
                    }
                    continue;
                }

                // IList
                if (typeof(IList).IsAssignableFrom(prop.PropertyType))
                {
                    if (tVal is null)
                    {
                        prop.SetValue(target, oVal);
                        continue;
                    }

                    var tList = (IList)tVal;
                    var oList = (IList)oVal;
                    foreach (var item in oList) tList.Add(item);
                    continue;
                }

                // scalars/complex refs: overwrite
                prop.SetValue(target, oVal);
            }
        }

        private static bool IsStringKeyDictionary(Type type, out Type[] genArgs)
        {
            genArgs = Type.EmptyTypes;

            // Handle IDictionary<string, T> or concrete types implementing it
            var dictIface = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                ? type
                : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

            if (dictIface is null) return false;

            var args = dictIface.GetGenericArguments();
            if (args.Length == 2 && args[0] == typeof(string))
            {
                genArgs = args;
                return true;
            }
            return false;
        }
    }
}