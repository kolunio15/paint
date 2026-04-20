using Microsoft.AspNetCore.Mvc;
using Paint;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();

builder.Services.AddSingleton<RoomConnections>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseWebSockets();

app.MapStaticAssets();

app.UseAuthorization();

app.MapRazorPages()
    .WithStaticAssets();

app.Map("/room_ws", async (HttpContext context, [FromQuery] string roomId, [FromServices] RoomConnections connections) => {
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await connections.HandleConnection(roomId, socket);
});

app.Run();