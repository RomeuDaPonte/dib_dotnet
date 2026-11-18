using Dib4t.Client.Api;
using Dib4t.Client.Auth;
using Dib4t.Client.Theme;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddMudServices(options =>
{
    // Legacy react-toastify notifications: top right (PLAN.md §8.2).
    options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopRight;
});

// Auth (PLAN.md §4.6, §8.2). Components never touch HttpClient: they use AuthSession / the typed API clients.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<TokenStore>();
// Handlers: the token header (and "Invalid token." → logged out), then the global error notification (§8.2).
builder.Services.AddScoped(provider => new HttpClient(new AuthHeaderHandler(provider.GetRequiredService<TokenStore>())
{
    InnerHandler = new ErrorNotificationHandler(provider.GetRequiredService<ISnackbar>())
    {
        InnerHandler = new HttpClientHandler(),
    },
})
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<IAccountApi, AccountApi>();
builder.Services.AddScoped<IUsersApi, UsersApi>();
builder.Services.AddScoped<IPricesApi, PricesApi>();
builder.Services.AddScoped<IPaymentMethodsApi, PaymentMethodsApi>();
builder.Services.AddScoped<IPrinterSettingsApi, PrinterSettingsApi>();
builder.Services.AddScoped<IEntityApi, EntityApi>();
builder.Services.AddScoped<IBudgetApi, BudgetApi>();
builder.Services.AddScoped<ITaskApi, TaskApi>();
builder.Services.AddScoped<IJobApi, JobApi>();
builder.Services.AddTransient<Dib4t.Client.Budgets.BudgetDetailsState>();
builder.Services.AddScoped<Dib4t.Client.Budgets.IClipboard, Dib4t.Client.Budgets.Clipboard>();
builder.Services.AddScoped<Dib4t.Client.Budgets.IFileDownload, Dib4t.Client.Budgets.FileDownload>();
builder.Services.AddScoped<ILogsApi, LogsApi>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuthSession>();
builder.Services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<AuthSession>());
builder.Services.AddScoped<ThemeState>();

await builder.Build().RunAsync();
