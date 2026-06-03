using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paint;
using Paint.Data;
using Paint.Middleware;
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

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    // KnownNetworks/KnownProxies are cleared so the headers set by the trusted
    // reverse proxy in front of the app are honored. Only enable this when the app
    // is actually deployed behind a proxy you control.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddScoped<IRoomRepository, EfRoomRepository>();
builder.Services.AddScoped<IArtworkRepository, EfArtworkRepository>();
builder.Services.AddScoped<BanService>();
builder.Services.AddScoped<ModerationService>();
builder.Services.AddSingleton<RoomConnections>();
builder.Services.AddHostedService<ContentDeletionService>();
builder.Services.AddControllers();
builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var dbContext = services.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await EnsureRoomSchemaAsync(dbContext);
    await EnsureModerationSchemaAsync(dbContext);
    await EnsureDefaultDataAsync(services);
}

app.UseForwardedHeaders();

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
app.UseMiddleware<BanCheckMiddleware>();
app.UseAuthorization();

app.MapRazorPages()
    .WithStaticAssets();
app.MapControllers();

app.Map("/room_ws", async (
    HttpContext context,
    [FromQuery] string? roomId,
    [FromQuery] string? password,
    [FromServices] RoomConnections connections,
    [FromServices] ApplicationDbContext db) =>
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

    var roomEntity = await db.Rooms.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == requestedRoomId, context.RequestAborted);
    if (roomEntity is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (roomEntity.IsProtected)
    {
        var isModOrAdmin = context.User.IsInRole("Admin") || context.User.IsInRole("Moderator");
        if (!isModOrAdmin)
        {
            if (string.IsNullOrEmpty(password))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var hasher = new PasswordHasher<Room>();
            var result = hasher.VerifyHashedPassword(roomEntity, roomEntity.PasswordHash!, password);
            if (result == PasswordVerificationResult.Failed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
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
        roomEntity.CanvasWidth,
        roomEntity.CanvasHeight,
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

static async Task EnsureModerationSchemaAsync(ApplicationDbContext dbContext)
{
    if (!dbContext.Database.IsSqlite())
        return;

    var connection = dbContext.Database.GetDbConnection();
    await dbContext.Database.OpenConnectionAsync();

    try
    {
        await AddColumnIfMissingAsync(connection, "AspNetUsers", "IsBanned", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, "AspNetUsers", "BannedUntil", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "AspNetUsers", "BanReason", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "AspNetUsers", "LastKnownIp", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "AspNetUsers", "LastKnownDeviceToken", "TEXT NULL");

        await AddColumnIfMissingAsync(connection, "Artworks", "IsHidden", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, "Artworks", "HiddenAt", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Artworks", "HiddenById", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Artworks", "ScheduledDeletionAt", "TEXT NULL");

        await AddColumnIfMissingAsync(connection, "Comments", "IsHidden", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, "Comments", "HiddenAt", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Comments", "HiddenById", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Comments", "ScheduledDeletionAt", "TEXT NULL");

        await AddColumnIfMissingAsync(connection, "Rooms", "IsHidden", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, "Rooms", "HiddenAt", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Rooms", "HiddenById", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Rooms", "ScheduledDeletionAt", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Rooms", "SnapshotPath", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "Rooms", "SnapshotGlobalId", "INTEGER NOT NULL DEFAULT -1");

        await EnsureTableAsync(connection, "ModerationQueue", """
            CREATE TABLE ModerationQueue (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Type INTEGER NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                TargetUserId TEXT NULL,
                TargetArtworkId INTEGER NULL,
                TargetCommentId INTEGER NULL,
                TargetRoomId INTEGER NULL,
                ReportedById TEXT NULL,
                ReportReason TEXT NULL,
                ReviewedById TEXT NULL,
                ReviewedAt TEXT NULL,
                ReviewNote TEXT NULL,
                CreatedAt TEXT NOT NULL
            )
            """);

        await EnsureTableAsync(connection, "ModerationActions", """
            CREATE TABLE ModerationActions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ModeratorId TEXT NOT NULL,
                Type INTEGER NOT NULL,
                TargetUserId TEXT NULL,
                TargetArtworkId INTEGER NULL,
                TargetCommentId INTEGER NULL,
                TargetRoomId INTEGER NULL,
                Reason TEXT NULL,
                ExpiresAt TEXT NULL,
                CreatedAt TEXT NOT NULL
            )
            """);

        await EnsureTableAsync(connection, "BannedIdentifiers", """
            CREATE TABLE BannedIdentifiers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Type INTEGER NOT NULL,
                ValueHash TEXT NOT NULL,
                OriginalUserId TEXT NULL,
                BannedById TEXT NOT NULL,
                Note TEXT NULL,
                CreatedAt TEXT NOT NULL
            )
            """);
    }
    finally
    {
        await dbContext.Database.CloseConnectionAsync();
    }
}

static async Task AddColumnIfMissingAsync(System.Data.Common.DbConnection connection, string table, string column, string definition)
{
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var cmd = connection.CreateCommand())
    {
        cmd.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
    }

    if (!columns.Contains(column))
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await cmd.ExecuteNonQueryAsync();
    }
}

static async Task EnsureTableAsync(System.Data.Common.DbConnection connection, string table, string createSql)
{
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'";
    var result = await cmd.ExecuteScalarAsync();
    if (result is null)
    {
        cmd.CommandText = createSql;
        await cmd.ExecuteNonQueryAsync();
    }
}

static async Task EnsureDefaultDataAsync(IServiceProvider services)
{
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var dbContext = services.GetRequiredService<ApplicationDbContext>();
    var config = services.GetRequiredService<IConfiguration>();

    foreach (var role in new[] { "Admin", "Moderator" })
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    var adminUserName = config["AdminUser:UserName"] ?? "admin";
    var adminPassword = config["AdminUser:Password"] ?? "admin123";

    var adminUser = await userManager.FindByNameAsync(adminUserName);
    if (adminUser is null)
    {
        adminUser = new ApplicationUser
        {
            UserName = adminUserName,
            DisplayName = adminUserName,
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true
        };
        var result = await userManager.CreateAsync(adminUser, adminPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Could not create admin user: {errors}");
        }
    }

    if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
        await userManager.AddToRoleAsync(adminUser, "Admin");

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
