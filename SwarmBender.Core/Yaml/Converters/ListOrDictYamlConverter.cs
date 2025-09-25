using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

/// <summary>
/// list | map birliğini lossless işler:
///  - list: ["K=V", "FLAG", "X="]
///  - map:  { K: "V", FLAG: "", X: "" }
/// </summary>
public sealed class ListOrDictYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ListOrDict);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var list = new List<string>();
            while (!parser.Accept<SequenceEnd>(out _))
                list.Add(parser.Consume<Scalar>().Value);
            parser.Consume<SequenceEnd>();
            return ListOrDict.FromList(list);
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (!parser.Accept<MappingEnd>(out _))
            {
                var k = parser.Consume<Scalar>().Value;
                var v = parser.TryConsume<Scalar>(out var sval) ? sval.Value : string.Empty;
                map[k] = v ?? string.Empty;
            }
            parser.Consume<MappingEnd>();
            return ListOrDict.FromMap(map);
        }

        throw new YamlException("Expected sequence or mapping for ListOrDict.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var v = (ListOrDict?)value;

        if (v?.AsMap is { Count: > 0 })
        {
            emitter.Emit(new MappingStart());
            foreach (var kv in v.AsMap)
            {
                emitter.Emit(new Scalar(kv.Key));
                emitter.Emit(new Scalar(kv.Value ?? string.Empty));
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