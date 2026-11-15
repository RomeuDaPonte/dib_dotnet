using Dib4t.Application;
using Dib4t.Infrastructure;
using Dib4t.Server.Auth;
using Dib4t.Server.Components;
using Dib4t.Server.Http;
using Dib4t.Server.Logging;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddDib4tAuth(builder.Configuration);
builder.AddDib4tLogging();
builder.Services.AddExceptionHandler<LegacyExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();
app.LogLifetime();

// Request id + one log entry per API request first, so they cover the error responses below (PLAN.md §3.3).
app.UseDib4tRequestLogging();
app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = LegacyExceptionHandler.WriteFallbackAsync });

// Unknown client routes re-execute to the client's "not found" page; API routes keep their own (legacy) responses.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api")
        && context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
    {
        statusCodePages.Enabled = false;
    }

    return next(context);
});

// The token is read only for [Authorize] endpoints (AuthSetup.ReadTokenHeader).
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Dib4t.Client.Routes).Assembly);

await app.RunAsync();
