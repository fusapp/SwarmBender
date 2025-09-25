using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

public class ServiceNetworksYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ServiceNetworks);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // short: list of scalars
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var list = new List<string>();
            while (!parser.Accept<SequenceEnd>(out _))
                list.Add(parser.Consume<Scalar>().Value);
            parser.Consume<SequenceEnd>();
            return ServiceNetworks.FromList(list);
        }

        // long: map name -> attachment
        if (parser.Accept<MappingStart>(out _))
        {
            var map = (Dictionary<string, ServiceNetworkAttachment>?)
                      rootDeserializer(typeof(Dictionary<string, ServiceNetworkAttachment>))
                      ?? new Dictionary<string, ServiceNetworkAttachment>(StringComparer.OrdinalIgnoreCase);

            return ServiceNetworks.FromMap(map);
        }

        throw new YamlException("Expected sequence or mapping for 'networks'.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var v = (ServiceNetworks?)value;
        if (v?.AsMap is { Count: > 0 })
        {
            serializer(v.AsMap, typeof(Dictionary<string, ServiceNetworkAttachment>));
            return;
        }

        // default to list
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
        if (v?.AsList is { Count: > 0 })
            foreach (var n in v.AsList) emitter.Emit(new Scalar(n));
        emitter.Emit(new SequenceEnd());
    }
}