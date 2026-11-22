using MarkUp.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(apiBase)) apiBase = builder.HostEnvironment.BaseAddress;
if (!apiBase.EndsWith('/')) apiBase += "/";

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddScoped<Browser>();
builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<OfflineSync>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<ConfirmService>();
builder.Services.AddScoped<TrackingClient>();

var host = builder.Build();
await host.Services.GetRequiredService<AuthSession>().InitializeAsync();
await host.RunAsync();