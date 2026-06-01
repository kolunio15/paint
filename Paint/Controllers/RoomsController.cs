using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Paint;
using Paint.Contracts;
using Paint.Services;

namespace Paint.Controllers;

[ApiController]
[Route("api/rooms")]
public class RoomsController : ControllerBase
{
    private readonly IRoomRepository _rooms;
    private readonly RoomConnections _roomConnections;

    public RoomsController(IRoomRepository rooms, RoomConnections roomConnections)
    {
        _rooms = rooms;
        _roomConnections = roomConnections;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoomSummaryDto>>> GetRooms(CancellationToken cancellationToken)
    {
        var rooms = await _rooms.GetRoomsAsync(_roomConnections.GetActiveUserCounts(), cancellationToken);
        return Ok(rooms);
    }

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<RoomSummaryDto>> CreateRoom(
        [FromBody] CreateRoomRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var room = await _rooms.CreateRoomAsync(request, userId, cancellationToken);
        var summary = new RoomSummaryDto(
            room.Id,
            room.Name,
            0,
            room.MaxUsers,
            room.CanvasWidth,
            room.CanvasHeight,
            room.IsProtected);

        return Created($"/api/rooms/{room.Id}", summary);
    }

    [Authorize]
    [HttpDelete("{roomId:int}")]
    public async Task<IActionResult> DeleteRoom(int roomId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        try
        {
            var deleted = await _rooms.DeleteRoomAsync(roomId, userId, cancellationToken);
            return deleted ? NoContent() : NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [Authorize]
    [HttpGet("{roomId:int}/events")]
    public async Task<ActionResult<IReadOnlyList<RoomEventDto>>> GetRoomEvents(
        int roomId,
        [FromQuery] int startGlobalId = 0,
        [FromQuery] int? endGlobalId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _rooms.RoomExistsAsync(roomId, cancellationToken))
        {
            return NotFound();
        }

        var events = await _rooms.GetRoomEventsAsync(roomId, startGlobalId, endGlobalId, cancellationToken);
        return Ok(events);
    }

    [Authorize]
    [HttpPost("{roomId:int}/events")]
    public async Task<ActionResult<RoomEventDto>> AddRoomEvent(
        int roomId,
        [FromBody] AddRoomEventRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!await _rooms.RoomExistsAsync(roomId, cancellationToken))
        {
            return NotFound();
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var savedEvent = await _rooms.AddRoomEventAsync(
            roomId,
            request.Content,
            userId,
            cancellationToken);

        return Created($"/api/rooms/{roomId}/events/{savedEvent.GlobalEventId}", savedEvent);
    }
}
