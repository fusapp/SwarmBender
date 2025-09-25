using System.Globalization;
using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

public class UlimitsYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Ulimits);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (!parser.TryConsume<MappingStart>(out _))
            throw new YamlException("Expected mapping for 'ulimits'.");

        var u = new Ulimits();
        while (!parser.Accept<MappingEnd>(out _))
        {
            var name = parser.Consume<Scalar>().Value;

            if (parser.TryConsume<Scalar>(out var scalar))
            {
                if (int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                    u.Map[name] = UlimitEntry.FromSingle(iv);
                else
                    throw new YamlException($"ulimit '{name}' must be an integer.");
            }
            else if (parser.TryConsume<MappingStart>(out _))
            {
                var entry = new UlimitEntry();
                while (!parser.Accept<MappingEnd>(out _))
                {
                    var k = parser.Consume<Scalar>().Value;
                    var v = parser.Consume<Scalar>().Value;
                    if (k == "soft" && int.TryParse(v, out var s)) entry.Soft = s;
                    else if (k == "hard" && int.TryParse(v, out var h)) entry.Hard = h;
                    else throw new YamlException($"ulimit '{name}' invalid key '{k}'.");
                }
                parser.Consume<MappingEnd>();
                u.Map[name] = entry;
            }
            else
            {
                throw new YamlException($"ulimit '{name}' must be scalar or mapping.");
            }
        }
        parser.Consume<MappingEnd>();
        return u;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var u = (Ulimits?)value ?? new Ulimits();
        emitter.Emit(new MappingStart());
        foreach (var (name, entry) in u.Map)
        {
            emitter.Emit(new Scalar(name));
            if (!entry.IsObject && entry.Single.HasValue)
            {
                emitter.Emit(new Scalar(entry.Single.Value.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                emitter.Emit(new MappingStart());
                if (entry.Soft.HasValue)
                {
                    emitter.Emit(new Scalar("soft"));
                    emitter.Emit(new Scalar(entry.Soft.Value.ToString(CultureInfo.InvariantCulture)));
                }
                if (entry.Hard.HasValue)
                {
                    emitter.Emit(new Scalar("hard"));
                    emitter.Emit(new Scalar(entry.Hard.Value.ToString(CultureInfo.InvariantCulture)));
                }
                emitter.Emit(new MappingEnd());
            }
        }
        emitter.Emit(new MappingEnd());
    }
}