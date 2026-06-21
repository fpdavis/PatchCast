using PatchCast.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);
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
builder.Services.AddSingleton<ServerCertificateProvider>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
