using PatchCast.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PatchCast Audio Service");
builder.Services.Configure<PatchCastOptions>(builder.Configuration.GetSection(PatchCastOptions.SectionName));
builder.Services.AddSingleton<ServerCertificateProvider>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
