using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

public class ExtraHostsYamlConverter: IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ExtraHosts);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // short syntax: sequence of "host:ip"
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var list = new List<string>();
            while (!parser.Accept<SequenceEnd>(out _))
                list.Add(parser.Consume<Scalar>().Value);
            parser.Consume<SequenceEnd>();
            return ExtraHosts.FromList(list);
        }

        // long syntax: mapping host -> ip
        if (parser.TryConsume<MappingStart>(out _))
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (!parser.Accept<MappingEnd>(out _))
            {
                var host = parser.Consume<Scalar>().Value;
                var ip   = parser.Consume<Scalar>().Value;
                map[host] = ip;
            }
            parser.Consume<MappingEnd>();
            return ExtraHosts.FromMap(map);
        }

        throw new YamlException("Expected sequence or mapping for 'extra_hosts'.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var v = (ExtraHosts?)value;

        if (v?.AsMap is { Count: > 0 })
        {
            emitter.Emit(new MappingStart());
            foreach (var kv in v.AsMap)
            {
                emitter.Emit(new Scalar(kv.Key));
                emitter.Emit(new Scalar(kv.Value));
            }
            emitter.Emit(new MappingEnd());
            return;
        }

        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
        if (v?.AsList is { Count: > 0 })
            foreach (var s in v.AsList) emitter.Emit(new Scalar(s));
        emitter.Emit(new SequenceEnd());
    }
}