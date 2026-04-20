var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// TODO: Register A2A server endpoints from RockBot framework

app.MapGet("/health", () => "ok");

app.Run();
