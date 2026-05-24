using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        var room = await _rooms.CreateRoomAsync(request, userId, cancellationToken);
        var summary = new RoomSummaryDto(
            room.Id,
            room.Name,
            0,
            room.MaxUsers,
            room.PixelWidth,
            room.PixelHeight,
            room.IsProtected);

        return Created($"/api/rooms/{room.Id}", summary);
    }

    [Authorize]
    [HttpDelete("{roomId}")]
    public async Task<IActionResult> DeleteRoom(string roomId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

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
    [HttpGet("{roomId}/events")]
    public async Task<ActionResult<IReadOnlyList<RoomEventDto>>> GetRoomEvents(
        string roomId,
        [FromQuery] long startGlobalId = 0,
        [FromQuery] long? endGlobalId = null,
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
    [HttpPost("{roomId}/events")]
    public async Task<ActionResult<RoomEventDto>> AddRoomEvent(
        string roomId,
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
        var userName = User.Identity?.Name;
        var savedEvent = await _rooms.AddRoomEventAsync(
            roomId,
            request.Content,
            userId,
            userName,
            request.GlobalEventId,
            cancellationToken);

        return Created($"/api/rooms/{roomId}/events/{savedEvent.GlobalEventId}", savedEvent);
    }

    [Authorize]
    [HttpGet("{roomId}/canvas-events")]
    public async Task<ActionResult<IReadOnlyList<CanvasEventDto>>> GetCanvasEvents(
        string roomId,
        CancellationToken cancellationToken)
    {
        if (!await _rooms.RoomExistsAsync(roomId, cancellationToken))
        {
            return NotFound();
        }

        var events = await _rooms.GetCanvasEventsAsync(roomId, cancellationToken);
        return Ok(events);
    }
}
