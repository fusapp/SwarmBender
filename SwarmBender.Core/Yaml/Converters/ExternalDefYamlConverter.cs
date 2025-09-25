using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

public class ExternalDefYamlConverter: IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ExternalDef);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // external: true|false
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (bool.TryParse(scalar.Value, out var b))
                return ExternalDef.FromBool(b);
            throw new YamlException($"'external' must be boolean or mapping; got '{scalar.Value}'.");
        }

        // external: { name: actual }
        if (parser.TryConsume<MappingStart>(out _))
        {
            string? name = null;
            while (!parser.Accept<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>().Value;
                var val = parser.Consume<Scalar>().Value;
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                    name = val;
            }
            parser.Consume<MappingEnd>();
            return name is null ? ExternalDef.FromBool(true) : ExternalDef.FromName(name);
        }

        throw new YamlException("Expected scalar or mapping for 'external'.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var v = (ExternalDef?)value;
        if (string.IsNullOrWhiteSpace(v?.Name))
        {
            emitter.Emit(new Scalar((v?.AsBool ?? false) ? "true" : "false"));
            return;
        }

        emitter.Emit(new MappingStart());
        emitter.Emit(new Scalar("name"));
        emitter.Emit(new Scalar(v!.Name));
        emitter.Emit(new MappingEnd());
    }
}