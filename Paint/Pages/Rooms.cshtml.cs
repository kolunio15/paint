using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Paint.Pages;

public record Room(string Id, string Name, int UserCount, int MaxUsers, bool IsProtected);

public class RoomsModel : PageModel
{
    public List<Room> ActiveRooms { get; set; } = [];

    public void OnGet()
    {
        ActiveRooms = [
            new Room("pokoj1", "SONIC OC ART TRADES (16+)", 12, 20, false),
            new Room("pro-room", "(DE) Pixel Art Chill", 4, 10, false),
            new Room("pixelart", "Ship your OCs 2!!", 8, 15, false),
            new Room("test", "Pusty pokój testowy", 0, 5, true)
        ];
    }
}