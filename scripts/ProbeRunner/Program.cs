using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: ProbeRunner <executable> [arguments...]");
    return 2;
}

var start = new ProcessStartInfo(args[0]) { UseShellExecute = false };
// Share an attached terminal (including Ctrl+C). A background caller can supply
// its own redirected handles without allocating a new console for the child.
if (OperatingSystem.IsWindows())
{
    start.CreateNoWindow = GetConsoleCP() == 0;
    // A GUI apphost can fail before the managed probe starts (e.g. missing runtime).
    start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
}
foreach (var argument in args.Skip(1)) start.ArgumentList.Add(argument);

try
{
    using var child = Process.Start(start)!;
    child.WaitForExit();
    return child.ExitCode;
}
catch (Win32Exception error)
{
    Console.Error.WriteLine($"ProbeRunner could not start '{start.FileName}': {error.Message}");
    return 1;
}

[DllImport("kernel32.dll", ExactSpelling = true)]
static extern uint GetConsoleCP();
