using System;
using System.Collections.Generic;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using SwarmBender.Core.Data.Compose;

public sealed class ListOrStringYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ListOrString);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return ListOrString.FromString(scalar.Value ?? string.Empty);
        }

        if (parser.TryConsume<SequenceStart>(out var _))
        {
            var list = new List<string>();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                // strings inside the sequence
                var item = rootDeserializer(typeof(string)) as string ?? string.Empty;
                list.Add(item);
            }
            return ListOrString.FromList(list);
        }

        throw new YamlException("Expected scalar or sequence for ListOrString.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var los = value as ListOrString;

        if (los?.AsList is { Count: > 0 } list)
        {
            // Emit as FLOW sequence: [ "CMD", "curl", ... ]
            emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Flow));
            foreach (var s in list)
            {
                emitter.Emit(new Scalar(null, null, s ?? string.Empty, ScalarStyle.DoubleQuoted, true, false));
            }
            emitter.Emit(new SequenceEnd());
            return;
        }

        // Single string case
        var str = los?.AsString ?? string.Empty;
        emitter.Emit(new Scalar(null, null, str, ScalarStyle.DoubleQuoted, true, false));
    }
}