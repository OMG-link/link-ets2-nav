using System.Runtime.ExceptionServices;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Map;

/// <summary>TruckLib catches sector parsing failures. Observe only this synchronous reader's thread and active sector.</summary>
internal sealed class SectorReadEvidence:IDisposable
{
    private readonly int thread=Environment.CurrentManagedThreadId;
    private SectorCoordinate? current;
    private bool observing;
    private readonly Dictionary<SectorCoordinate,string> failures=[];
    public IReadOnlyDictionary<SectorCoordinate,string> Failures=>failures;
    public SectorReadEvidence()=>AppDomain.CurrentDomain.FirstChanceException+=Observe;
    public void Begin(SectorCoordinate sector)=>current=sector;
    public void End()=>current=null;
    private void Observe(object? sender,FirstChanceExceptionEventArgs args)
    {
        if(observing||Environment.CurrentManagedThreadId!=thread||current is not {} sector)return;
        observing=true;
        try
        {
            bool inSector=new System.Diagnostics.StackTrace().GetFrames()?.Any(f=>
                f.GetMethod() is {} method&&method.DeclaringType==typeof(TruckLib.ScsMap.Map)
                    &&method.Name is "ReadSector" or "ReadSectors")==true;
            if(inSector)failures.TryAdd(sector,args.Exception.GetType().Name+": "+args.Exception.Message);
        }
        catch { /* The observer must never change the native exception's propagation. */ }
        finally { observing=false; }
    }
    public void Dispose()=>AppDomain.CurrentDomain.FirstChanceException-=Observe;
}

public sealed class MapSectorReadException(IReadOnlyDictionary<SectorCoordinate,string> failures)
    :IOException("Map sector parsing did not complete: "+string.Join("; ",failures.Select(p=>$"{p.Key}: {p.Value}")))
{
    public IReadOnlyDictionary<SectorCoordinate,string> Failures {get;}=new System.Collections.ObjectModel.ReadOnlyDictionary<SectorCoordinate,string>(failures.ToDictionary());
}
