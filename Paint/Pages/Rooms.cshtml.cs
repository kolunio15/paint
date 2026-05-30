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
        var rooms = await _rooms.GetRoomsAsync(_roomConnections.GetActiveUserCounts(), cancellationToken);
        ActiveRooms = rooms.ToList();
    }
}
