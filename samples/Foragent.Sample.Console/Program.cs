// Minimal A2A client sample that demonstrates calling Foragent.
// TODO: Implement A2A client call once agent endpoints are available.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Foragent sample client — see docs/capabilities.md for planned capabilities.");

app.Run();
