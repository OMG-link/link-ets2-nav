using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using TruckLib.Models.Ppd;

/// <summary>Standalone SVG of native PPD geometry and explicit connectivity; no inferred road markings.</summary>
internal static class PrefabSvgExporter
{
    private static readonly XNamespace Ns="http://www.w3.org/2000/svg";
    private static readonly string[] Colors=["#2473bd","#df7b18","#15917f","#a24ab3","#c44761","#5d7830"];
    private static string F(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
    private static Vector3 At(NavCurve c,float t)
    {
        float t2=t*t,t3=t2*t;
        return (2*t3-3*t2+1)*c.StartPosition+(t3-2*t2+t)*Vector3.Transform(-Vector3.UnitZ,c.StartRotation)*c.Length
            +(-2*t3+3*t2)*c.EndPosition+(t3-t2)*Vector3.Transform(-Vector3.UnitZ,c.EndRotation)*c.Length;
    }
    public static void Write(PrefabDescriptor d,string source,string output)
    {
        bool Valid(int i)=>i>=0&&i<d.NavCurves.Count;
        var samples=d.NavCurves.SelectMany(c=>Enumerable.Range(0,65).Select(i=>At(c,i/64f))).Concat(d.Nodes.Select(n=>n.Position)).ToArray();
        if(samples.Length==0||samples.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Z)))throw new InvalidDataException("Empty or nonfinite PPD geometry.");
        float minX=samples.Min(p=>p.X),maxX=samples.Max(p=>p.X),minZ=samples.Min(p=>p.Z),maxZ=samples.Max(p=>p.Z);
        double scale=Math.Min(650/Math.Max(1,maxX-minX),660/Math.Max(1,maxZ-minZ));
        Vector2 XY(Vector3 p)=>new((float)(440+(p.X-(minX+maxX)/2)*scale),(float)(465+(p.Z-(minZ+maxZ)/2)*scale));
        string P(Vector3 p){var xy=XY(p);return F(xy.X)+","+F(xy.Y);}
        XElement Text(double x,double y,string text,int size=14)=>new(Ns+"text",new XAttribute("x",F(x)),new XAttribute("y",F(y)),new XAttribute("font-size",size),text);
        var owners=Enumerable.Range(0,d.NavCurves.Count).ToDictionary(i=>i,_=>new HashSet<int>());
        var reachable=new Dictionary<int,HashSet<int>>();
        HashSet<int> Walk(int start)
        {
            if(reachable.TryGetValue(start,out var cached))return cached;
            var seen=new HashSet<int>();var pending=new Stack<int>();pending.Push(start);
            while(pending.TryPop(out int current))if(Valid(current)&&seen.Add(current))foreach(int next in d.NavCurves[current].NextLines)pending.Push(next);
            return reachable[start]=seen;
        }
        var matrix=new List<string>();
        for(int node=0;node<d.Nodes.Count;node++)
        for(int slot=0;slot<d.Nodes[node].InputLines.Length;slot++)
        {
            int start=d.Nodes[node].InputLines[slot];if(!Valid(start))continue;
            var seen=Walk(start);foreach(int curve in seen)owners[curve].Add(node);
            var exits=new List<string>();
            for(int to=0;to<d.Nodes.Count;to++)
            for(int lane=0;lane<d.Nodes[to].OutputLines.Length;lane++)
                if(seen.Contains(d.Nodes[to].OutputLines[lane]))exits.Add($"N{to}/出{lane}");
            matrix.Add($"N{node}/入{slot} → "+(exits.Count==0?"无可达出口":string.Join("，",exits)));
        }
        int footer=1000+Math.Max(0,matrix.Count-12)*25;
        int height=footer+(int)Math.Ceiling(d.NavCurves.Count/4d)*25+115;
        var svg=new XElement(Ns+"svg",new XAttribute("width",1450),new XAttribute("height",height),new XAttribute("viewBox",$"0 0 1450 {height}"));
        svg.Add(new XElement(Ns+"style","text{font-family:'Microsoft YaHei',sans-serif;fill:#25334a}.id{font-size:11px;paint-order:stroke;stroke:white;stroke-width:3px;stroke-linejoin:round}"));
        svg.Add(new XElement(Ns+"rect",new XAttribute("width",1450),new XAttribute("height",height),new XAttribute("fill","#f7f9fc")));
        svg.Add(Text(28,36,"预制件导航线路 · 真实几何与有向连接",25),Text(28,65,source,13),
            Text(28,91,$"控制节点 {d.Nodes.Count} 个 · 导航曲线 {d.NavCurves.Count} 条 · 编号均从 0 开始 · 上 −Z / 右 +X（局部坐标）"));
        svg.Add(new XElement(Ns+"rect",new XAttribute("x",25),new XAttribute("y",110),new XAttribute("width",830),new XAttribute("height",745),new XAttribute("rx",10),new XAttribute("fill","white"),new XAttribute("stroke","#dce4ee")));
        double span=Math.Max(maxX-minX,maxZ-minZ);
        double step=span>0?Math.Max(5,Math.Pow(10,Math.Floor(Math.Log10(span/5)))):5;
        for(double x=Math.Floor(minX/step)*step;x<=maxX+step;x+=step)
        {
            var a=XY(new((float)x,0,minZ));var b=XY(new((float)x,0,maxZ));
            svg.Add(new XElement(Ns+"path",new XAttribute("d",$"M{F(a.X)},{F(a.Y)} L{F(b.X)},{F(b.Y)}"),new XAttribute("stroke","#e8edf4")));
        }
        for(double z=Math.Floor(minZ/step)*step;z<=maxZ+step;z+=step)
        {
            var a=XY(new(minX,0,(float)z));var b=XY(new(maxX,0,(float)z));
            svg.Add(new XElement(Ns+"path",new XAttribute("d",$"M{F(a.X)},{F(a.Y)} L{F(b.X)},{F(b.Y)}"),new XAttribute("stroke","#e8edf4")));
        }
        for(int i=0;i<d.NavCurves.Count;i++)
        {
            var c=d.NavCurves[i];string color=owners[i].Count==1?Colors[owners[i].Single()%Colors.Length]:"#74536f";
            var first=c.StartPosition+Vector3.Transform(-Vector3.UnitZ,c.StartRotation)*c.Length/3;
            var second=c.EndPosition-Vector3.Transform(-Vector3.UnitZ,c.EndRotation)*c.Length/3;
            svg.Add(new XElement(Ns+"path",new XAttribute("d",$"M{P(c.StartPosition)} C{P(first)} {P(second)} {P(c.EndPosition)}"),
                new XAttribute("fill","none"),new XAttribute("stroke",color),new XAttribute("stroke-width",2.3),new XAttribute("data-curve-index",i),
                new XAttribute("data-next",string.Join(',',c.NextLines)),new XElement(Ns+"title",$"曲线 {i}；后继 [{string.Join(',',c.NextLines)}]；前驱 [{string.Join(',',c.PreviousLines)}]；长度 {F(c.Length)} m")));
            var mid=XY(At(c,.52f));var before=XY(At(c,.5f));var after=XY(At(c,.54f));var tangent=after-before;
            if(tangent.LengthSquared()>.0001f)
            {
                tangent=Vector2.Normalize(tangent);var normal=new Vector2(-tangent.Y,tangent.X);
                string Points(params Vector2[] points)=>string.Join(' ',points.Select(p=>F(p.X)+","+F(p.Y)));
                svg.Add(new XElement(Ns+"polygon",new XAttribute("points",Points(mid+tangent*5,mid-tangent*4+normal*3,mid-tangent*4-normal*3)),new XAttribute("fill",color)));
            }
            var label=Text(mid.X+5,mid.Y-5,i.ToString(CultureInfo.InvariantCulture));label.Add(new XAttribute("class","id"));svg.Add(label);
            var end=XY(c.EndPosition);svg.Add(new XElement(Ns+"circle",new XAttribute("cx",F(end.X)),new XAttribute("cy",F(end.Y)),new XAttribute("r",2.8),new XAttribute("fill","white"),new XAttribute("stroke",color)));
        }
        for(int i=0;i<d.Nodes.Count;i++)
        {
            var p=XY(d.Nodes[i].Position);
            svg.Add(new XElement(Ns+"rect",new XAttribute("x",F(p.X-5)),new XAttribute("y",F(p.Y-5)),new XAttribute("width",10),new XAttribute("height",10),new XAttribute("fill","white"),new XAttribute("stroke","#25334a")),Text(p.X+12,p.Y+20,$"N{i}",18));
        }
        svg.Add(Text(880,137,"入口车道 → 可达出口车道",21),Text(880,164,"以下为 NextLines 可达性；不代表允许自由换道。",13));
        for(int i=0;i<matrix.Count;i++)svg.Add(Text(880,199+i*25,matrix[i],13));
        int legend=Math.Max(550,225+matrix.Count*25);
        svg.Add(Text(880,legend,"颜色：能到达曲线的入口节点；共享曲线为紫色。",13),Text(880,legend+29,"数字：NavCurve 索引；箭头：行驶方向。",13),
            Text(880,legend+58,"小圆点：曲线端点；方框：控制节点参考位置。",13),Text(880,legend+87,"线条相交不等于连接，以后继列表为准。",13));
        svg.Add(Text(30,895,$"网格间距 {F(step)} 米；XY 等比例。使用三次 Bézier 精确表达 Hermite 曲线。",14),Text(30,925,"线路表示 PPD 导航曲线，不是道路边缘或车道标线；控制节点参考位置不是所有车道的交点。",14),Text(30,footer-24,"曲线后继表（— 表示无后继）",19));
        for(int i=0;i<d.NavCurves.Count;i++)
            svg.Add(Text(30+(i%4)*350,footer+(i/4)*25,$"{i} → "+(d.NavCurves[i].NextLines.Count==0?"—":string.Join(", ",d.NavCurves[i].NextLines)),14));
        new XDocument(new XDeclaration("1.0","utf-8",null),svg).Save(output);
        foreach(string row in matrix)Console.WriteLine(row);
    }
}
