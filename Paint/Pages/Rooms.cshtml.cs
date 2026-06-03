using Microsoft.AspNetCore.Mvc.RazorPages;
using Paint;
using Paint.Contracts;
using Paint.Services;

namespace Paint.Pages;

public class RoomsModel : PageModel
{
    private readonly IRoomRepository _rooms;
    private readonly RoomConnections _roomConnections;

    public RoomsModel(IRoomRepository rooms, RoomConnections roomConnections)
    {
        _rooms = rooms;
        _roomConnections = roomConnections;
    }

    public List<RoomSummaryDto> ActiveRooms { get; set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var includeHidden = User.IsInRole("Admin") || User.IsInRole("Moderator");
        var rooms = await _rooms.GetRoomsAsync(_roomConnections.GetActiveUserCounts(), includeHidden, cancellationToken);
        ActiveRooms = rooms.ToList();
    }
}
