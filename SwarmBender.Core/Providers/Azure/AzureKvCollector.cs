using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using SwarmBender.Core.Data.Models;

namespace SwarmBender.Core.Providers.Azure;

public sealed class AzureKvCollector : IAzureKvCollector
{
    public AzureKvCollector()
    {
    }

    public async Task<Dictionary<string, string>> CollectAsync(
        ProvidersAzureKv cfg,
        string env,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Güvenli kısa devre
        if (cfg is null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.VaultUrl))
            return result;

        var client = new SecretClient(new Uri(cfg.VaultUrl), new DefaultAzureCredential());

        // Tüm secret property’leri gez (yalnızca etkin olanları oku)
        await foreach (var props in client.GetPropertiesOfSecretsAsync().WithCancellation(ct))
        {
            if (ct.IsCancellationRequested) break;
            if (props.Enabled != true) continue;

            try
            {
                // Son sürüm değerini al
                KeyVaultSecret secret = await client.GetSecretAsync(props.Name);

                // İsim normalizasyonu: replace tablosunu TERSTEN uygula
                // Örn: {"__":"--"} ise "ConnectionStrings--Default" -> "ConnectionStrings__Default"
                var normalizedName = ApplyReverseReplace(props.Name, cfg.Replace);

                // İstersen env’e göre filtrelemek istersen burada bir koşul ekleyebilirsin
                // (örn: normalizedName içinde env aramak gibi) — şimdilik hepsini döndürüyoruz.
                result[normalizedName] = secret.Value;
            }
            catch (RequestFailedException)
            {
                // tekil secret okunamadıysa yut ve devam et
            }
        }

        return result;
    }

    private static string ApplyReverseReplace(string input, Dictionary<string, string> replace)
    {
        if (replace is null || replace.Count == 0) return input;

        // Değerleri uzunluğa göre sırala (çakışmaları minimize etmek için)
        // value -> key ters dönüşümü: örn "--" -> "__"
        string s = input;
        foreach (var kv in replace.OrderByDescending(kv => kv.Value.Length))
        {
            var value = kv.Value;
            var key = kv.Key;
            if (!string.IsNullOrEmpty(value))
            {
                s = s.Replace(value, key, StringComparison.Ordinal);
            }
        }

        return s;
    }
}