using System.Collections.ObjectModel;
using TruckLib.Sii;

namespace LinkEts2Nav.Map;

/// <summary>Parsed once; consumers borrow units without mutating them.</summary>
public sealed class GameSiiDocument
{
    public string Path {get;}
    public IReadOnlyList<Unit> Units {get;}
    public IReadOnlyList<string> Includes {get;}
    private readonly IReadOnlyDictionary<string,IReadOnlyList<Unit>> byClass;
    private readonly IReadOnlyDictionary<string,IReadOnlyList<Unit>> byName;
    internal GameSiiDocument(string path,SiiFile file)
    {
        Path=path;Units=Array.AsReadOnly(file.Units.ToArray());
        Includes=Array.AsReadOnly(file.Includes.Order(StringComparer.Ordinal).ToArray());
        byClass=new ReadOnlyDictionary<string,IReadOnlyList<Unit>>(Units.GroupBy(u=>u.Class).ToDictionary(g=>g.Key,g=>(IReadOnlyList<Unit>)Array.AsReadOnly(g.ToArray()),StringComparer.Ordinal));
        byName=new ReadOnlyDictionary<string,IReadOnlyList<Unit>>(Units.GroupBy(u=>u.Name).ToDictionary(g=>g.Key,g=>(IReadOnlyList<Unit>)Array.AsReadOnly(g.ToArray()),StringComparer.Ordinal));
    }
    public IReadOnlyList<Unit> OfClass(string name)=>byClass.GetValueOrDefault(name)??[];
    public IReadOnlyList<Unit> Named(string name)=>byName.GetValueOrDefault(name)??[];
}
