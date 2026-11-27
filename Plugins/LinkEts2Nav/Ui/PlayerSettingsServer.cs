using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinkEts2Nav.Ui;

public sealed class PlayerSettingsServer : IAsyncDisposable
{
    private readonly WebApplication app;
    public Uri Address { get; }

    private PlayerSettingsServer(WebApplication app, string capability)
    {
        this.app = app;
        Address = new UriBuilder(app.Urls.Single()) { Fragment = capability }.Uri;
    }

    public static async Task<PlayerSettingsServer> StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        { Args = [], EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        // The surrounding desktop or standalone host owns Ctrl+C and process exit.
        builder.Services.AddSingleton<IHostLifetime, SettingsLifetime>();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();
        try
        {
            string capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            byte[] credential = Encoding.ASCII.GetBytes("Bearer " + capability);
            app.Use(async (context, next) =>
            {
                var request = context.Request;
                string host = "127.0.0.1:" + context.Connection.LocalPort;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                if (request.Host.Value != host) { context.Response.StatusCode = 403; return; }
                if (request.Path.StartsWithSegments("/api"))
                {
                    string origin = request.Headers.Origin.ToString();
                    if (origin.Length > 0 && origin != "http://" + host) { context.Response.StatusCode = 403; return; }
                    string supplied = request.Headers.Authorization.ToString();
                    if (supplied.Length != credential.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), credential))
                    { context.Response.StatusCode = 401; return; }
                    if (HttpMethods.IsPost(request.Method) && !request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
                }
                try { await next(context); }
                catch (JsonException) { await Problem(context, 400, "请求格式有误。"); }
                catch (BadHttpRequestException ex) { await Problem(context, ex.StatusCode, "请求内容无效或过大。"); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            });
            Asset(app, "/", "index.html", "text/html; charset=utf-8");
            Asset(app, "/settings.css", "settings.css", "text/css; charset=utf-8");
            Asset(app, "/settings.js", "settings.js", "text/javascript; charset=utf-8");
            app.MapGet("/api/status", Status);
            app.MapPost("/api/speech/generate", Generate);
            app.MapPost("/api/speech/cancel", () => SpeechPackGeneration.Current.TryCancel()
                ? Results.Ok() : Message(409, "当前没有正在生成的语音包。"));
            app.MapPost("/api/speech/import", Import);
            app.MapPost("/api/replay", SaveReplay);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return new(app, capability);
        }
        catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static void Asset(WebApplication app, string route, string name, string type)
    {
        string resource = "LinkEts2Nav.Ui." + name;
        using var check = typeof(PlayerSettingsServer).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing player settings page resource: " + name);
        app.MapGet(route, () => Results.Stream(typeof(PlayerSettingsServer).Assembly.GetManifestResourceStream(resource)!, type));
    }

    private static IResult Status(HttpRequest request)
    {
        var nav = NavDiagnostics.Current;
        var job = SpeechPackGeneration.Current.Status;
        SpeechPackSettingView? pack = null;
        string? packError = null;
        if (request.Query.ContainsKey("pack"))
            try { pack = SpeechPackSettings.Current.Read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            { packError = "现有语音配置无法读取。"; }
        var maneuver = nav.Navigation?.Next;
        string? next = maneuver is null ? null : maneuver.Instruction ?? NavigationPrompts.Action(maneuver.Kind);
        int? nextMeters = maneuver is not null && double.IsFinite(maneuver.DistanceMeters)
            ? (int)Math.Clamp(Math.Round(maneuver.DistanceMeters), 0, int.MaxValue) : null;
        return Results.Json(new
        {
            navigation = nav.State,
            route = nav.RouteState,
            next,
            nextMeters,
            replay = ReplayCapture.IsAvailable,
            activeDirectory = SpeechPackSettings.Current.ActiveDirectory,
            pack,
            packError,
            job = new { phase = job.Phase.ToString(), job.Completed, job.Total, job.Message,
                budget = job.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture),
                spentTokens = job.SpentTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                job.WholeCount, job.ComposedCount }
        });
    }

    private static async Task<IResult> Generate(HttpRequest request, CancellationToken token)
    {
        var input = await request.ReadFromJsonAsync<SpeechPackGenerationRequest>(token);
        if (input is null) return Message(400, "请填写生成设置。");
        if (input.ValidationError() is { } invalid) return Message(400, invalid);
        return SpeechPackGeneration.Current.TryStart(input) ? Results.Accepted() : Message(409, "已有语音包正在生成。");
    }

    private sealed record ImportInput(string? Directory);
    private static async Task<IResult> Import(HttpRequest request, CancellationToken token)
    {
        var input = await request.ReadFromJsonAsync<ImportInput>(token);
        if (string.IsNullOrWhiteSpace(input?.Directory)) return Message(400, "请填写本机的语音包目录。");
        try
        {
            var pack = await SpeechPackGeneration.Current.SelectPackAsync(input.Directory, token);
            return Results.Json(new { message = $"已应用语音包，包含 {pack.Fixed.Count} 条语句和 {pack.Names.Count} 个名称。" });
        }
        catch (DirectoryNotFoundException) { return Message(400, "找不到这个目录，请检查路径。"); }
        catch (UnauthorizedAccessException) { return Message(400, "无法读取语音包或写入配置，请检查目录权限。"); }
        catch (InvalidOperationException) { return Message(409, "这个目录的语音包正在写入，请等待生成结束。"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException)
        { return Message(400, "语音包无法应用，请检查清单、音频文件和配置目录。"); }
    }

    private sealed record ReplayInput(string? Comment);
    private static async Task<IResult> SaveReplay(HttpRequest request, CancellationToken token)
    {
        var input = await request.ReadFromJsonAsync<ReplayInput>(token);
        if (input?.Comment?.Length > 4000) return Message(400, "回放备注最多 4000 字。");
        if (!ReplayCapture.IsAvailable) return Message(409, "当前没有可保存的驾驶回放。");
        try { return Results.Json(new { message = "回放已保存到 " + await ReplayCapture.SaveAsync(input?.Comment) }); }
        catch (InvalidOperationException) { return Message(409, "尚无可保存的驾驶轨迹，或另一份回放正在保存。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Message(500, "无法保存回放，请检查磁盘空间和目录权限。"); }
    }

    private static IResult Message(int status, string message) => Results.Json(new { message }, statusCode: status);
    private static Task Problem(HttpContext context, int status, string message) => Message(status, message).ExecuteAsync(context);

    public async ValueTask DisposeAsync()
    {
        try { await app.StopAsync().ConfigureAwait(false); }
        finally
        {
            try { await SpeechPackGeneration.Current.CancelAndWaitAsync().ConfigureAwait(false); }
            finally { await app.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private sealed class SettingsLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
