namespace LinkEts2Nav.Core;

/// <summary>Measured traversal distances, with no cycle-to-window substitution.</summary>
internal static class LaneTraceDistance
{
    internal static double Positive(double meters,string source)
    {
        if(!double.IsFinite(meters)||meters<=0)
            throw new ArgumentException($"{source} must have a finite positive measured length (actual {meters}).");
        return meters;
    }
    internal static double Advance(double current,double meters)
    {
        double next=current+meters;
        // The initial query prefix can be zero. Every traversed road/path is
        // positive; failure to represent that progress is invalid input too.
        if(!double.IsFinite(current)||!double.IsFinite(meters)||meters<0||!double.IsFinite(next)
            ||meters>0&&next<=current)
            throw new ArgumentException($"Lane trace cannot advance from {current} by {meters} measured metres.");
        return next;
    }
}
