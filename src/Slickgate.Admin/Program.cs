using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using MudBlazor.Services;
using ReactiveUI.Builder;
using ReactiveUI.Primitives.Concurrency;
using Slickgate.Admin;
using Slickgate.Admin.Services;
using Slickgate.Admin.ViewModels;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();

builder.Services.AddSingleton(sp => new GatewaySettings(
    sp.GetRequiredService<IJSRuntime>(),
    builder.Configuration["Gateway:BaseUrl"]));

builder.Services.AddScoped(_ => new HttpClient());
builder.Services.AddScoped<GatewayAdminClient>();

// ViewModels. ReactiveComponentBase disposes the ViewModel when the component is
// navigated away from, so they must be transient — a scoped instance would be
// handed back already-disposed on the second visit to a page.
builder.Services.AddTransient<DashboardViewModel>();
builder.Services.AddTransient<RoutesViewModel>();
builder.Services.AddTransient<MappingsViewModel>();
builder.Services.AddTransient<ContainersViewModel>();
builder.Services.AddTransient<SettingsViewModel>();

RxAppBuilder.CreateReactiveUIBuilder()
    .WithBlazor()
    .WithMainThreadScheduler(Sequencer.CurrentThread)
    .WithTaskPoolScheduler(Sequencer.CurrentThread)
    .BuildApp();

await builder.Build().RunAsync();
