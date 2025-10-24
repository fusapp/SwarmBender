using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Yaml;
using SwarmBender.Core.Yaml.Converters;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SwarmBender.Core.IO;

public sealed class YamlEngine : IYamlEngine
{
    private readonly IDeserializer _d = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithTypeConverter(new ExternalDefYamlConverter())
        .WithTypeConverter(new ExtraHostsYamlConverter())
        .WithTypeConverter(new ListOrDictYamlConverter())
        .WithTypeConverter(new ListOrStringYamlConverter())
        // .WithTypeConverter(new PortMappingYamlConverter())
        .WithTypeConverter(new ServiceNetworksYamlConverter())
        .WithTypeConverter(new SysctlsYamlConverter())
        .WithTypeConverter(new UlimitsYamlConverter())
       
        .Build();

    private readonly ISerializer _s = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitNull)
        .WithTypeConverter(new ExternalDefYamlConverter())
        .WithTypeConverter(new ExtraHostsYamlConverter())
        .WithTypeConverter(new ListOrDictYamlConverter())
        .WithTypeConverter(new ListOrStringYamlConverter())
        // .WithTypeConverter(new PortMappingYamlConverter())
        .WithTypeConverter(new ServiceNetworksYamlConverter())
        .WithTypeConverter(new SysctlsYamlConverter())
        .WithTypeConverter(new UlimitsYamlConverter())
        .WithEventEmitter(inner => new QuotingEventEmitter(inner))
        .Build();

    public IDictionary<string, object?> LoadToMap(string yamlText)
    {
        using var reader = new StringReader(yamlText);
        var obj = _d.Deserialize<object?>(reader);
        return ToDict(obj);
    }

    public string DumpFromMap(IDictionary<string, object?> map)
        => _s.Serialize(map);

    public Task<string> DumpYamlAsync<T>(T data)
    {
        return Task.FromResult(_s.Serialize(data));
    }

    private static IDictionary<string, object?> ToDict(object? node)
    {
        if (node is IDictionary<object, object?> dict)
        {
            var res = new Dictionary<string, object?>();
            foreach (var kv in dict)
            {
                var k = kv.Key?.ToString() ?? "";
                res[k] = ToAny(kv.Value);
            }
            return res;
        }
        return new Dictionary<string, object?>();
    }

    private static object? ToAny(object? v)
    {
        if (v is IDictionary<object, object?> d) return ToDict(d);
        if (v is IList<object?> l)
        {
            var arr = new List<object?>();
            foreach (var it in l) arr.Add(ToAny(it));
            return arr;
        }
        return v;
    }
    
    public async Task<T?> LoadYamlAsync<T>(string path, CancellationToken ct)
    {
        using var sr = new StreamReader(path);
        var text = await sr.ReadToEndAsync();
        ct.ThrowIfCancellationRequested();
        return _d.Deserialize<T>(text);
    }

    public async Task<object?> LoadYamlAsync(string path, CancellationToken ct)
    {
        using var sr = new StreamReader(path);
        var text = await sr.ReadToEndAsync();
        ct.ThrowIfCancellationRequested();
        return _d.Deserialize<object?>(text);
    }
}