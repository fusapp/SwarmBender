using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SwarmBender.Services;


/// <summary>
/// Writes FlowSeq as YAML flow sequence: [a, b, c]
/// </summary>
public sealed class FlowSeqYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => typeof(FlowSeq).IsAssignableFrom(type);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer nestedObjectDeserializer)
        => throw new NotSupportedException("Deserialization of FlowSeq is not supported.");

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer nestedObjectSerializer)
    {
        var list = (FlowSeq)(value ?? new FlowSeq());
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Flow));
        foreach (var item in list)
        {
            nestedObjectSerializer(item);
        }
        emitter.Emit(new SequenceEnd());
    }
}