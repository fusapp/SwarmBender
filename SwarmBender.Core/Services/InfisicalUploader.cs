using System.Text;
using System.Text.RegularExpressions;
using Infisical.Sdk;
using Infisical.Sdk.Model;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Services;

public sealed class InfisicalUploader : IInfisicalUploader
{
    private readonly ISecretDiscovery _discovery;
    private readonly IOutput _out;

    public InfisicalUploader(ISecretDiscovery discovery, IOutput @out)
    {
        _discovery = discovery;
        _out = @out;
    }

    public async Task<UploadReport> UploadAsync(
        string repoRoot, string stackId, string env, SbConfig cfg,
        bool force = false, bool dryRun = false, bool showValues = false, CancellationToken ct = default)
    {
        var items = new List<UploadItemResult>();
        int created = 0, updated = 0, skippedSame = 0, skippedOther = 0, skippedFiltered = 0, skippedMissing = 0;

        var inf = cfg.Providers?.Infisical;
        if (inf is not { Enabled: true })
        {
            _out.Warn("Infisical provider disabled; nothing to do.");
            return new(created, updated, skippedSame, skippedOther, skippedFiltered, skippedMissing, items);
        }

        var projectId = inf.WorkspaceId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _out.Error("providers.infisical.workspaceId (ProjectId) boş.");
            return new(created, updated, skippedSame, skippedOther, skippedFiltered, skippedMissing, items);
        }

        // Discovery anahtarları KANONİK gelir ('.' -> '__')
        var discovered = await _discovery.DiscoverAsync(repoRoot, stackId, env, cfg, ct);
        if (discovered.Count == 0)
        {
            _out.Info("No secrets discovered.");
            return new(created, updated, skippedSame, skippedOther, skippedFiltered, skippedMissing, items);
        }

        var envSlug = (inf.EnvMap != null && inf.EnvMap.TryGetValue(env, out var mapped) &&
                       !string.IsNullOrWhiteSpace(mapped))
            ? mapped
            : env;

        // === include + key normalize (Uploader tarafı) ===
        // Provider’a yazılacak key: KeyTemplate → Replace (__ token’ına) → (gerekirse) reverse "__"
        var prepared = new List<(string ProviderKey, string Value)>();
        foreach (var s in discovered)
        {
            var canonicalKey = s.Key ?? string.Empty; // örn: ConnectionStrings__MSSQL__Master
            var value = s.Value ?? string.Empty;

            // include: Collector ile aynı eşleşme kombinasyonları
            var revOrig        = ReverseDoubleUnderscore(canonicalKey, inf);
            var templOrig      = ApplyKeyTemplate(canonicalKey, inf.KeyTemplate);
            var templRev       = ApplyKeyTemplate(revOrig, inf.KeyTemplate);
            var replTemplOrig  = ApplyReplacements(templOrig, inf.Replace);
            var replTemplRev   = ApplyReplacements(templRev,  inf.Replace);

            if (!IsIncludedEx(canonicalKey, revOrig, replTemplOrig, replTemplRev, inf.Include, inf.Replace))
            {
                items.Add(new UploadItemResult(canonicalKey, null, value, "skipped-filtered"));
                skippedFiltered++;
                continue;
            }

            // Provider’a yazılacak anahtar: KeyTemplate → Replace
            // (Örn "__" → "_" gibi; Infisical deposundaki key)
            var providerKey = replTemplOrig;

            prepared.Add((providerKey, value));
        }

        if (prepared.Count == 0)
            return new(created, updated, skippedSame, skippedOther, skippedFiltered, skippedMissing, items);

        if (dryRun)
        {
            foreach (var (k, v) in prepared)
                _out.WriteKeyValue(k, v, mask: !showValues);
            return new(created, updated, skippedSame, skippedOther, skippedFiltered, skippedMissing, items);
        }

        // Auth
        var settingsBuilder = new InfisicalSdkSettingsBuilder();
        if (!string.IsNullOrWhiteSpace(inf.BaseUrl))
            settingsBuilder = settingsBuilder.WithHostUri(inf.BaseUrl);
        var client = new InfisicalClient(settingsBuilder.Build());

        if (!await EnsureAuthAsync(client, _out, ct))
        {
            foreach (var (k, v) in prepared)
            {
                items.Add(new UploadItemResult(k, null, v, "skipped-missing-token"));
                skippedMissing++;
            }
            return new(created, updated, skippedSame, skippedOther, skippedFiltered, skippedMissing, items);
        }

        // === Per-key routing + upsert (throttled) ===
        var throttler = new SemaphoreSlim(8);
        int total = prepared.Count, done = 0;
        foreach (var (providerKey, newValue) in prepared)
        {
            await throttler.WaitAsync(ct);
            _ = Task.Run(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(newValue))
                    {
                        Interlocked.Increment(ref skippedOther);
                        lock (items) items.Add(new UploadItemResult(providerKey, null, newValue, "skipped-empty"));
                        return;
                    }

                    // RoutePlan üret (Infisical özel routes; yoksa pathTemplate fallback)
                    var plan = SecretRoutePlanner.Build(
                        inf.Routes, inf.PathTemplate ?? "/{stackId}_all", stackId, envSlug, providerKey);

                    // 1) readPaths üzerinde ilk mevcut değeri bul
                    Secret? existing = null;
                    foreach (var rp in plan.ReadPaths)
                    {
                        var rpn = NormalizePath(rp);
                        try
                        {
                            existing = await client.Secrets().GetAsync(new GetSecretOptions
                            {
                                ProjectId = projectId,
                                EnvironmentSlug = envSlug,
                                SecretPath = rpn,
                                SecretName = providerKey
                            });
                            if (existing is not null) break;
                        }
                        catch (InfisicalException)
                        {
                            _out.Info($"{rpn}/{providerKey} was not found");
                            // yok → sıradaki
                        }
                    }

                    // 2) writePath’e upsert
                    var wpn = NormalizePath(plan.WritePath);
                    _out.Info($"{wpn}/{providerKey} writing");

                    if (!force && existing is not null &&
                        string.Equals(existing.SecretValue, newValue, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref skippedSame);
                        lock (items)
                            items.Add(new UploadItemResult(providerKey, existing.SecretValue, newValue, "skipped-same"));
                    }
                    else if (existing is null)
                    {
                        await client.Secrets().CreateAsync(new CreateSecretOptions
                        {
                            ProjectId = projectId,
                            EnvironmentSlug = envSlug,
                            SecretPath = wpn,
                            SecretName = providerKey,
                            SecretValue = newValue
                        });

                        Interlocked.Increment(ref created);
                        lock (items) items.Add(new UploadItemResult(providerKey, null, newValue, "created"));
                    }
                    else
                    {
                        await client.Secrets().UpdateAsync(new UpdateSecretOptions
                        {
                            ProjectId = projectId,
                            EnvironmentSlug = envSlug,
                            SecretPath = wpn,
                            SecretName = providerKey,
                            NewSecretValue = newValue
                        });

                        Interlocked.Increment(ref updated);
                        lock (items) items.Add(new UploadItemResult(providerKey, existing.SecretValue, newValue, "updated"));
                    }
                }
                catch (InfisicalException ex)
                {
                    Interlocked.Increment(ref skippedOther);
                    lock (items) items.Add(new UploadItemResult(providerKey, null, newValue, "error:" + ex.Message + " " + ex.InnerException?.Message));
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref skippedOther);
                    lock (items) items.Add(new UploadItemResult(providerKey, null, newValue, "error: " + ex.Message));
                }
                finally
                {
                    var n = Interlocked.Increment(ref done);
                    if (n % 10 == 0 || n == total) _out.Info($"progress {n}/{total}");
                    throttler.Release();
                }
            }, ct);
        }

        // tüm görevler bitsin
        while (Volatile.Read(ref done) < total) await Task.Delay(50, ct);
        return new(created, updated, skippedSame, skippedOther, skippedFiltered, skippedMissing, items);
    }

    // --- helpers ---
    private static async Task<bool> EnsureAuthAsync(InfisicalClient client, IOutput output, CancellationToken ct)
    {
        try
        {
            // Universal Auth (Machine Identity)
            var clientId = Environment.GetEnvironmentVariable("INFISICAL_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("INFISICAL_CLIENT_SECRET");
            if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
            {
                await client.Auth().UniversalAuth().LoginAsync(clientId, clientSecret);
                output.Success("Login Success");
                return true;
            }
            return false;
        }
        catch (InfisicalException ex)
        {
            output.Error($"Infisical login failed: {ex.Message}");
            if (ex.InnerException != null) output.Info(ex.InnerException.Message);
            return false;
        }
        catch (Exception ex)
        {
            output.Error($"Infisical login error: {ex.Message}");
            return false;
        }
    }

    private static string ApplyKeyTemplate(string key, string? template)
        => string.IsNullOrWhiteSpace(template) ? key : template.Replace("{key}", key, StringComparison.Ordinal);

    private static string ApplyReplacements(string key, Dictionary<string, string>? replace)
    {
        if (replace is null || replace.Count == 0) return key;
        var s = key;
        foreach (var kv in replace)
            s = s.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
        return s;
    }

    private static bool Glob(string text, string pattern)
    {
        if (pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Collector ile aynı mantıkta include: 4 varyasyon
    /// patt vs canonical, patt vs reversed, pattRepl vs templ+repl(canonical), pattRepl vs templ+repl(reversed)
    /// </summary>
    private static bool IsIncludedEx(
        string canonicalKey,
        string reversedKey,
        string templReplCanonical,
        string templReplReversed,
        List<string>? include,
        Dictionary<string, string>? replace)
    {
        if (include is null || include.Count == 0) return true;

        foreach (var patt in include)
        {
            var pattRepl = ApplyReplacements(patt, replace);

            if (Glob(canonicalKey, patt) ||
                Glob(reversedKey, patt) ||
                Glob(templReplCanonical, pattRepl) ||
                Glob(templReplReversed, pattRepl))
                return true;
        }
        return false;
    }

    private static string ReverseDoubleUnderscore(string input, ProvidersInfisical cfg)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (cfg.Replace != null && cfg.Replace.TryGetValue("__", out var token) && !string.IsNullOrEmpty(token))
            return input.Replace(token, "__");
        return input;
    }

    private static string NormalizePath(string? p)
    {
        // null/boş → "/"
        if (string.IsNullOrWhiteSpace(p)) return "/";

        // başına "/" ekle, birden fazla "/" yi tekille
        var s = p.Trim();
        if (!s.StartsWith("/")) s = "/" + s;
        s = Regex.Replace(s, "/{2,}", "/");

        // kök ise tamam
        if (s == "/") return s;

        // segment bazlı temizle
        var segs = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
        {
            // sadece [A-Za-z0-9_-] kalsın, diğerlerini "_" yap
            var cleaned = Regex.Replace(segs[i], "[^A-Za-z0-9_-]", "_");
            if (string.IsNullOrEmpty(cleaned)) cleaned = "_";
            segs[i] = cleaned;
        }
        return "/" + string.Join('/', segs);
    }
}