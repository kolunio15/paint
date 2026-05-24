using Microsoft.EntityFrameworkCore;
using Paint.Models;

namespace Paint.Data;

public static class RoomSeedData
{
    public static async Task EnsureSeedRoomsAsync(ApplicationDbContext dbContext)
    {
        if (await dbContext.Rooms.AnyAsync())
        {
            return;
        }

        dbContext.Rooms.AddRange(
            new PaintRoom
            {
                Id = "pokoj1",
                Name = "SONIC OC ART TRADES (16+)",
                MaxUsers = 20,
                PixelWidth = 1024,
                PixelHeight = 768
            },
            new PaintRoom
            {
                Id = "pro-room",
                Name = "(DE) Pixel Art Chill",
                MaxUsers = 10,
                PixelWidth = 1024,
                PixelHeight = 768
            },
            new PaintRoom
            {
                Id = "pixelart",
                Name = "Ship your OCs 2!!",
                MaxUsers = 15,
                PixelWidth = 1024,
                PixelHeight = 768
            },
            new PaintRoom
            {
                Id = "test",
                Name = "Pusty pokoj testowy",
                MaxUsers = 5,
                PixelWidth = 1024,
                PixelHeight = 768,
                IsProtected = true
            });

        await dbContext.SaveChangesAsync();
    }
}
