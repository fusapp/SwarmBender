using SwarmBender.Core.Data.Compose;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Core.Yaml.Converters;

public class ListOrStringYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ListOrString);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
            return ListOrString.FromString(scalar.Value);

        if (parser.TryConsume<SequenceStart>(out _))
        {
            var list = new List<string>();
            while (!parser.Accept<SequenceEnd>(out _))
                list.Add(parser.Consume<Scalar>().Value);
            parser.Consume<SequenceEnd>();
            return ListOrString.FromList(list);
        }

        throw new YamlException("Expected scalar or sequence for ListOrString.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var v = (ListOrString?)value;

        if (v?.AsList is { Count: > 0 })
        {
            emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
            foreach (var s in v.AsList) emitter.Emit(new Scalar(s));
            emitter.Emit(new SequenceEnd());
            return;
        }

        emitter.Emit(new Scalar(v?.AsString ?? string.Empty));
    }
}