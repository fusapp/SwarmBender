namespace SwarmBender.Services;

/// <summary>
/// Marker list to force YAML flow sequence style (e.g., ["CMD", "curl"]).
/// </summary>
public sealed class FlowSeq : List<object?>
{
    public FlowSeq() { }
    public FlowSeq(IEnumerable<object?> items) : base(items) { }
}