namespace LinkEts2Nav.Runtime;

public static class FerryNavigationPrompts
{
    public const string Generic="进入码头，按导航方向乘坐轮渡";
    public static string Passage(string from,string to,IReadOnlySet<string>? spokenNames)
        =>spokenNames?.Contains(from)==true&&spokenNames.Contains(to)
            ?$"进入{from}码头，乘坐前往{to}的轮渡":Generic;
}
