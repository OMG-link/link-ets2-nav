using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LinkEts2Nav.Probe
{
    internal static class ProbeCrashHandling
    {
        private const uint SemFailCriticalErrors = 0x0001;
        private const uint SemNoGpFaultErrorBox = 0x0002;
        private const uint SemNoOpenFileErrorBox = 0x8000;
        private const uint WerAlwaysShowUi = 0x10;
        private const uint WerNoUi = 0x20;
        private const int ErrorNotFound = unchecked((int)0x80070490);

        [ModuleInitializer]
        internal static void Initialize()
        {
            if (!OperatingSystem.IsWindows()) return;

            // Set the process mode before Main starts probe work or creates any workers.
            SetErrorMode(GetErrorMode() | SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
            try
            {
                int result = WerGetFlags(new IntPtr(-1), out uint flags);
                // WER has no per-process registration until its first flag has been set.
                if (result == ErrorNotFound) flags = 0;
                else if (result < 0) return;
                // The installed error mode also suppresses fatal-error UI if WER rejects this update.
                _ = WerSetFlags((flags & ~WerAlwaysShowUi) | WerNoUi);
            }
            catch (EntryPointNotFoundException)
            {
                // A host without the optional WER exports still has the process error mode above.
            }
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint GetErrorMode();

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint SetErrorMode(uint mode);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int WerGetFlags(IntPtr process, out uint flags);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern int WerSetFlags(uint flags);
    }
}
