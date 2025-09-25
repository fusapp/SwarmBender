using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

public class SysctlsYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Sysctls);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (!parser.TryConsume<MappingStart>(out _))
            throw new YamlException("Expected mapping for 'sysctls'.");

        var s = new Sysctls();
        while (!parser.Accept<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            if (parser.TryConsume<Scalar>(out var val))
                s.Map[key] = val.Value;
            else
                throw new YamlException($"sysctl '{key}' must be a scalar.");
        }
        parser.Consume<MappingEnd>();
        return s;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var s = (Sysctls?)value ?? new Sysctls();
        emitter.Emit(new MappingStart());
        foreach (var (k, v) in s.Map)
        {
            emitter.Emit(new Scalar(k));
            emitter.Emit(new Scalar(v ?? string.Empty));
        }
        emitter.Emit(new MappingEnd());
    }
}