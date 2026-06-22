using PatchCast.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PatchCast Audio Service");
builder.Logging.ClearProviders();
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddEventLog(options => options.SourceName = "PatchCast Audio Service");
}
else
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
        options.UseUtcTimestamp = false;
        options.SingleLine = true;
    });
}

builder.Services.Configure<PatchCastOptions>(builder.Configuration.GetSection(PatchCastOptions.SectionName));
builder.Services.AddHostedService<Worker>();

var patchCastOptions = builder.Configuration.GetSection(PatchCastOptions.SectionName).Get<PatchCastOptions>() ?? new PatchCastOptions();
if (patchCastOptions.WebPort is < 1 or > 65535)
    throw new InvalidOperationException("PatchCast:WebPort must be between 1 and 65535.");
if (patchCastOptions.WebPort == patchCastOptions.Port)
    throw new InvalidOperationException("PatchCast:WebPort must differ from PatchCast:Port.");

// The certificate provider is created once and shared by Kestrel (for the browser
// HTTPS/WSS endpoint) and the TCP Worker, so both present the same certificate.
using (var certificateLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole()))
{
    var certificateProvider = new ServerCertificateProvider(certificateLoggerFactory.CreateLogger<ServerCertificateProvider>());
    var serverCertificate = certificateProvider.GetCertificate();
    builder.Services.AddSingleton(certificateProvider);
    builder.WebHost.ConfigureKestrel(kestrel =>
        kestrel.ListenAnyIP(patchCastOptions.WebPort, listenOptions => listenOptions.UseHttps(serverCertificate)));
}

var app = builder.Build();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Map("/ws", async context =>
{
    var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PatchCastOptions>>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PatchCast.WebSocket");
    await WebSocketEndpoint.HandleAsync(context, options.Value, logger);
});

await app.RunAsync();
