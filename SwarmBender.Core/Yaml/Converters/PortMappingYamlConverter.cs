using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

public sealed class PortMappingYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(PortMapping);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
            return PortMapping.TryParse(scalar.Value, out var pm) ? pm : new PortMapping();

        if (parser.Accept<MappingStart>(out _))
        {
            // rootDeserializer mevcut mapping'i PortMapping'e deserialize eder.
            return rootDeserializer(typeof(PortMapping));
        }

        throw new YamlException("Expected scalar or mapping for PortMapping");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        // Deterministik: uzun söz dizimini yaz
        serializer(value, typeof(PortMapping));
    }
}