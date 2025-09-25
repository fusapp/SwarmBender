namespace SwarmBender.Core.Abstractions;

public interface IYamlEngine
{
    IDictionary<string, object?> LoadToMap(string yamlText);
    string DumpFromMap(IDictionary<string, object?> map);
    
    Task<T?> LoadYamlAsync<T>(string path, CancellationToken ct);
    Task<object?> LoadYamlAsync(string path, CancellationToken ct);
}