using Passerelle;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Configuration.GetValue<bool>("Relay:LocalOnly"))
{
    void ConfigureRelay(RelayOptions options) =>
        options.ConnectionString = builder.Configuration["Relay:ConnectionString"]
            ?? throw new InvalidOperationException("Set Relay__ConnectionString or start with --Relay:LocalOnly=true.");

    if (builder.Configuration.GetValue<bool>("Relay:KeepLocalHttp"))
    {
        builder.WebHost.AddPasserelle(ConfigureRelay);
    }
    else
    {
        builder.WebHost.UsePasserelle(ConfigureRelay);
    }
}

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/echo", async context =>
{
    context.Response.ContentType = context.Request.ContentType ?? "application/octet-stream";
    await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestAborted);
});
app.Run();
