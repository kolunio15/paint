using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paint;
using Paint.Data;
using Paint.Models;
using Paint.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.Password.RequiredLength = 6;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Auth/Login";
    options.AccessDeniedPath = "/Auth/Login";
});

builder.Services.AddScoped<IRoomRepository, EfRoomRepository>();
builder.Services.AddScoped<IArtworkRepository, EfArtworkRepository>();
builder.Services.AddSingleton<RoomConnections>();
builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var dbContext = services.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await EnsureRoomSchemaAsync(dbContext);
    await EnsureDefaultDataAsync(services);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseWebSockets();

app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages()
    .WithStaticAssets();
app.MapControllers();

app.Map("/room_ws", async (
    HttpContext context,
    [FromQuery] string? roomId,
    [FromServices] RoomConnections connections,
    [FromServices] IRoomRepository rooms) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var requestedRoomId = PaintDefaults.TestRoomId;
    if (!string.IsNullOrWhiteSpace(roomId) &&
        !int.TryParse(roomId, NumberStyles.Integer, CultureInfo.InvariantCulture, out requestedRoomId))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var room = await rooms.GetRoomAsync(requestedRoomId, 0, context.RequestAborted);
    if (room is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var userName = context.User.Identity?.IsAuthenticated == true
        ? context.User.FindFirstValue(ClaimTypes.Name) ?? context.User.Identity?.Name ?? PaintDefaults.GuestUserName
        : PaintDefaults.GuestUserName;
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? PaintDefaults.GuestUserId;

    await connections.HandleConnection(
        requestedRoomId.ToString(CultureInfo.InvariantCulture),
        socket,
        userName,
        userId,
        room.CanvasWidth,
        room.CanvasHeight,
        context.RequestAborted);
});

app.Run();

static async Task EnsureRoomSchemaAsync(ApplicationDbContext dbContext)
{
    if (!dbContext.Database.IsSqlite())
    {
        return;
    }

    var connection = dbContext.Database.GetDbConnection();
    await dbContext.Database.OpenConnectionAsync();

    try
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('Rooms');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains(nameof(Room.CanvasWidth)))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE Rooms ADD COLUMN {nameof(Room.CanvasWidth)} INTEGER NOT NULL DEFAULT {PaintDefaults.CanvasWidth}");
        }

        if (!columns.Contains(nameof(Room.CanvasHeight)))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE Rooms ADD COLUMN {nameof(Room.CanvasHeight)} INTEGER NOT NULL DEFAULT {PaintDefaults.CanvasHeight}");
        }
    }
    finally
    {
        await dbContext.Database.CloseConnectionAsync();
    }
}

static async Task EnsureDefaultDataAsync(IServiceProvider services)
{
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var dbContext = services.GetRequiredService<ApplicationDbContext>();

    var guestUser = await userManager.FindByIdAsync(PaintDefaults.GuestUserId);
    if (guestUser is null)
    {
        guestUser = new ApplicationUser
        {
            Id = PaintDefaults.GuestUserId,
            UserName = PaintDefaults.GuestAccountUserName,
            DisplayName = PaintDefaults.GuestUserName,
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(guestUser);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException($"Could not create default guest user: {errors}");
        }
    }

    var testRoom = await dbContext.Rooms
        .FirstOrDefaultAsync(room => room.Id == PaintDefaults.TestRoomId);

    if (testRoom is null)
    {
        testRoom = new Room
        {
            Id = PaintDefaults.TestRoomId,
            Name = PaintDefaults.TestRoomName,
            MaxUsers = PaintDefaults.TestRoomMaxUsers,
            IsProtected = false,
            PasswordHash = null,
            CreatedAt = DateTime.UtcNow,
            OwnerId = guestUser.Id,
            CanvasWidth = PaintDefaults.CanvasWidth,
            CanvasHeight = PaintDefaults.CanvasHeight
        };

        dbContext.Rooms.Add(testRoom);
    }
    else
    {
        testRoom.Name = PaintDefaults.TestRoomName;
        testRoom.MaxUsers = Math.Max(testRoom.MaxUsers, PaintDefaults.TestRoomMaxUsers);
        testRoom.IsProtected = false;
        testRoom.PasswordHash = null;
        testRoom.OwnerId = guestUser.Id;
        testRoom.CanvasWidth = PaintDefaults.CanvasWidth;
        testRoom.CanvasHeight = PaintDefaults.CanvasHeight;
    }

    var hasParticipant = await dbContext.RoomParticipants.AnyAsync(participant =>
        participant.RoomId == PaintDefaults.TestRoomId &&
        participant.UserId == guestUser.Id);

    if (!hasParticipant)
    {
        dbContext.RoomParticipants.Add(new RoomParticipant
        {
            RoomId = PaintDefaults.TestRoomId,
            UserId = guestUser.Id,
            JoinedAt = DateTime.UtcNow
        });
    }

    await dbContext.SaveChangesAsync();
}
