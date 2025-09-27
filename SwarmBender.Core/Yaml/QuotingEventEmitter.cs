using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace SwarmBender.Core.Yaml;

public sealed class QuotingEventEmitter : ChainedEventEmitter
{
    public QuotingEventEmitter(IEventEmitter next) : base(next) { }

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        if (eventInfo.Source.Value is string s)
        {
            var looksBooleanOrNull =
                s.Equals("true",  StringComparison.OrdinalIgnoreCase) ||
                s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("null",  StringComparison.OrdinalIgnoreCase) ||
                s.Equals("yes",   StringComparison.OrdinalIgnoreCase) ||
                s.Equals("no",    StringComparison.OrdinalIgnoreCase) ||
                s.Equals("on",    StringComparison.OrdinalIgnoreCase) ||
                s.Equals("off",   StringComparison.OrdinalIgnoreCase);

            var hasEdgeSpace = s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]));

            if (looksBooleanOrNull || hasEdgeSpace)
                eventInfo.Style = ScalarStyle.DoubleQuoted; // "true"
        }

        base.Emit(eventInfo, emitter);
    }
}