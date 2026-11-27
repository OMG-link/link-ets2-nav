param([Parameter(Mandatory=$true)][string]$ProjectName)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $projectRoot 'ETS2LA'
$outputRoot = Join-Path $projectRoot 'artifacts\host-overrides'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
function Read-Source([string]$relative) {
    return [IO.File]::ReadAllText((Join-Path $sourceRoot $relative)).Replace("`r`n", "`n")
}
function Replace-Once([string]$text, [string]$needle, [string]$replacement) {
    if (($text.Split(@($needle), [StringSplitOptions]::None).Count - 1) -ne 1) {
        throw "Upstream compatibility check failed for $ProjectName. Expected one anchor: $needle"
    }
    return $text.Replace($needle, $replacement)
}
function Save-Override([string]$name, [string]$text) {
    $path = Join-Path $outputRoot $name
    if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -ne $text) {
        [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
    }
}
if ($ProjectName -eq 'TruckLib') {
    $text = Read-Source 'TruckLib\TruckLib\ScsMap\Node.cs'
    $before = @'
            envelope = new(position.X, position.Z, position.X, position.Z);
            if (Parent is Map map)
            {
                map.Nodes.Tree.Delete(this);
                map.Nodes.Tree.Insert(this);
            }
'@
    $after = @'
            var tree = (Parent as Map)?.Nodes.Tree;
            int count = tree?.Count ?? 0;
            tree?.Delete(this);
            envelope = new(position.X, position.Z, position.X, position.Z);
            if (tree is not null && tree.Count < count)
                tree.Insert(this);
'@
    $text = Replace-Once $text ($before.Replace("`r`n", "`n")) ($after.Replace("`r`n", "`n"))
    Save-Override 'TruckLibNode.cs' $text

    $text = Read-Source 'TruckLib\TruckLib\ScsMap\Map.cs'
    $before = @'
            Console.WriteLine($"Finished reading sectors with {failed} failures.");
            Console.WriteLine($"Expect missing map data and navigation errors in affected regions.");
'@
    $after = @'
            if (failed != 0 || !AppContext.TryGetSwitch("LinkEts2Nav.NavigationOnly", out bool navigationOnly) || !navigationOnly)
            {
                Console.WriteLine($"Finished reading sectors with {failed} failures.");
                Console.WriteLine($"Expect missing map data and navigation errors in affected regions.");
            }
'@
    $text = Replace-Once $text ($before.Replace("`r`n", "`n")) ($after.Replace("`r`n", "`n"))
    Save-Override 'TruckLibMap.cs' $text
}
if ($ProjectName -eq 'ETS2LA') {
    $text = Read-Source 'ETS2LA\Program.cs'
    $text = Replace-Once $text '        args = Utils.WaitForRestartParentProcess(args);' ''
    $anchor = '        Utils.InitializeTranslations();'
    $text = Replace-Once $text $anchor @'
        args = Utils.WaitForRestartParentProcess(args);
        using var instance = LocalHost.OpenInstanceLock(out bool firstInstance);
        if (!firstInstance)
        {
            Console.Error.WriteLine("This ETS2LA host is already running. Close it before starting another mode.");
            Environment.ExitCode = 1;
            return;
        }

        Utils.InitializeTranslations();
        if (args.Contains("--navigation-only", StringComparer.Ordinal))
        {
            Environment.ExitCode = LocalHost.RunNavigationOnly();
            return;
        }
        RunFullHost(args, currentVersion);
    }

    private static void RunFullHost(string[] args, string currentVersion)
    {
'@
    Save-Override 'HostProgram.cs' $text
}
if ($ProjectName -eq 'ETS2LA.Game') {
    $text = Read-Source 'ETS2LA.Game\Program.cs'
    $anchor = '        var camera = CameraProvider.Current;'
    $text = Replace-Once $text $anchor (@'
        if (AppContext.TryGetSwitch("LinkEts2Nav.NavigationOnly", out var navigationOnly) && navigationOnly)
        {
            var routes = NavigationProvider.Current;
            var signals = SemaphoreProvider.Current;
            return;
        }

'@ + [char]10 + $anchor)
    Save-Override 'GameHandler.cs' $text
}
if ($ProjectName -eq 'TruckLib.HashFs') {
    $path = Join-Path $projectRoot 'dev-host\TruckLib.HashFs\TruckLib.HashFs\HashFsV2Reader.cs'
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $text = Replace-Once $text '!EqualityComparer<IEntry>.Default.Equals(stored, entry)' @'
(!ReferenceEquals(stored, entry) && !EqualityComparer<IEntry>.Default.Equals(stored, entry)
                    && !NavigationArchiveEntry.MatchesOtherRepresentation(stored, entry))
'@
    $anchor = '            var meta = new MainMetadata();'
    $text = Replace-Once $text $anchor ($anchor + [char]10 + '            bool compact = AppContext.TryGetSwitch("LinkEts2Nav.NavigationOnly", out bool navigationOnly) && navigationOnly;')
    $anchor = '                mr.BaseStream.Position = entry.MetadataIndex * MetadataTableBlockSize;'
    $text = Replace-Once $text $anchor (@'
                if (compact && NavigationArchiveEntry.TryAdd(Entries, entry, metadataTableBuffer))
                    continue;

'@ + [char]10 + $anchor)
    Save-Override 'HashFsV2Reader.cs' $text
}
if ($ProjectName -eq 'ETS2LA.Networking') {
    $text = Read-Source 'ETS2LA.Networking\Networking.cs'
    $anchor = "public NetworkingClient()`n    {"
    $text = Replace-Once $text $anchor ($anchor + "`n        if (IsLocalDevelopmentHost()) return;")
    $text = Replace-Once $text 'public class NetworkingClient' 'public class NetworkingClient'
    $text = Replace-Once $text "public class NetworkingClient`n{" "public class NetworkingClient`n{`n    private static bool IsLocalDevelopmentHost() => true;"
    Save-Override 'Networking.cs' $text
    $text = Read-Source 'ETS2LA.Networking\Plugins\client.cs'
    $text = Replace-Once $text "public class PluginApiClient`n{" "public class PluginApiClient`n{`n    private static bool IsLocalDevelopmentHost() => true;"
    $anchor = "public async Task FetchAvailablePluginsAsync()`n    {"
    $text = Replace-Once $text $anchor ($anchor + "`n        if (IsLocalDevelopmentHost()) { AvailablePlugins = new(); return; }")
    $anchor = "public bool InstallPlugin(string pluginId, string? version = null)`n    {"
    $text = Replace-Once $text $anchor ($anchor + "`n        if (IsLocalDevelopmentHost()) { Log(`"Remote plugin installation is disabled in Link ETS2 Nav development host.`", NotificationLevel.Warning); return false; }")
    Save-Override 'PluginClient.cs' $text
}
if ($ProjectName -eq 'ETS2LA.Backend') {
    $text = Read-Source 'ETS2LA.Backend\PluginHandler\Handler.cs'
    $anchor = "public string[] DiscoverManualDlls(string path)`n    {"
    $text = Replace-Once $text $anchor ($anchor + @'

        // Only the project-owned plugin is discoverable in this development host.
        var localPlugin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Plugins", "LinkEts2Nav.dll"));
        if (path == "Plugins" || path == "Libraries")
            return path == "Plugins" && File.Exists(localPlugin) ? new[] { localPlugin } : Array.Empty<string>();
'@)
    $anchor = "public string[] DiscoverManifestDlls(PluginType type)`n    {"
    $text = Replace-Once $text $anchor ($anchor + "`n        if (type == PluginType.Plugin || type == PluginType.Library) return Array.Empty<string>();")
    Save-Override 'PluginHandler.cs' $text
}

if ($ProjectName -eq 'ETS2LA.State') {
    $text = Read-Source 'ETS2LA.State\Program.cs'
    $text = Replace-Once $text '    LaneAssistOnly' ('    LaneAssistOnly,' + [char]10 + '    NavigationOnly')
    $text = Replace-Once $text 'public DrivingMode DrivingMode { get; set; } = DrivingMode.FullSelfDriving;' 'public DrivingMode DrivingMode { get => DrivingMode.NavigationOnly; set { } }'
    $text = Replace-Once $text '{ DrivingMode.LaneAssistOnly, _("Lane Assist Only") }' '{ DrivingMode.LaneAssistOnly, _("Lane Assist Only") }, { DrivingMode.NavigationOnly, "Navigation only" }'
    $text = Replace-Once $text 'public bool EnableAssists { get; set; } = false;' 'public bool EnableAssists { get => false; set { } }'
    $text = Replace-Once $text 'public float DesiredSpeed { get; set; } = 0.0f;' 'public float DesiredSpeed { get => 0; set { } }'
    $text = Replace-Once $text '        Events.Current.Subscribe<float>("TelemetryEvents.SpeedLimitChanged", HandleSpeedLimitChanged);' ''
    foreach ($binding in @('Cancel', 'Increase', 'Decrease')) {
        $text = Replace-Once $text ("        ControlsBackend.Current.On(DefaultControls." + $binding + ".Id, Handle" + $binding + ");") ''
    }
    $text = Replace-Once $text '        assistanceSettings = AssistanceSettings.Current;' ''
    foreach ($unused in @('    private AssistanceSettings assistanceSettings;', '    private float lastSpeedLimit = 0f;')) {
        $text = Replace-Once $text $unused ''
    }
    $start = '    private void RoundToNearestUnit()'
    $end = '    // MARK: Map Data Related'
    $text = Replace-Once $text $start $start
    $text = Replace-Once $text $end $end
    $begin = $text.IndexOf($start, [StringComparison]::Ordinal)
    $finish = $text.IndexOf($end, [StringComparison]::Ordinal)
    if ($finish -le $begin) { throw 'Invalid upstream assistance section boundaries.' }
    $text = $text.Remove($begin, $finish - $begin)
    Save-Override 'ApplicationState.cs' $text
}
if ($ProjectName -eq 'ETS2LA.Controls') {
    foreach ($platform in @('Windows\SharpDXControlsBackend.cs', 'Linux\SDL3ControlsBackend.cs')) {
        $text = Read-Source ('ETS2LA.Controls\' + $platform)
        foreach ($binding in @('Increase); // RES', 'Decrease); // SET', 'Cancel); // MODE')) {
            $text = Replace-Once $text ('        RegisterControl(DefaultControls.' + $binding) ''
        }
        Save-Override ([IO.Path]::GetFileName($platform)) $text
    }
}
