using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Paint;
using Paint.Contracts;
using Paint.Data;
using Paint.Models;
using Paint.Services;

namespace Paint.Controllers;

[ApiController]
[Route("api/rooms")]
public class RoomsController : ControllerBase
{
    private readonly IRoomRepository _rooms;
    private readonly RoomConnections _roomConnections;
    private readonly ModerationService _moderation;
    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public RoomsController(IRoomRepository rooms, RoomConnections roomConnections, ModerationService moderation, ApplicationDbContext db, IWebHostEnvironment environment)
    {
        _rooms = rooms;
        _roomConnections = roomConnections;
        _moderation = moderation;
        _db = db;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoomSummaryDto>>> GetRooms(CancellationToken cancellationToken)
    {
        var includeHidden = User.IsInRole("Admin") || User.IsInRole("Moderator");
        var rooms = await _rooms.GetRoomsAsync(_roomConnections.GetActiveUserCounts(), includeHidden, cancellationToken);
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
        await _moderation.EnqueueAsync(QueueItemType.RoomCreated, targetRoomId: room.Id);

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
            if (deleted)
            {
                TryDeleteRoomSnapshotFiles(roomId);
            }

            return deleted ? NoContent() : NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    public record VerifyPasswordRequest(string? Password);

    [HttpPost("{roomId:int}/verify-password")]
    public async Task<IActionResult> VerifyPassword(int roomId, [FromBody] VerifyPasswordRequest req, CancellationToken cancellationToken)
    {
        var room = await _db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId, cancellationToken);
        if (room is null) return NotFound();
        if (!room.IsProtected) return Ok();

        if (string.IsNullOrEmpty(req.Password)) return Forbid();

        var hasher = new PasswordHasher<Room>();
        var result = hasher.VerifyHashedPassword(room, room.PasswordHash!, req.Password);
        return result == PasswordVerificationResult.Failed ? Forbid() : Ok();
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

    public record SnapshotRequest(string ImageBase64, int GlobalId);

    // Compaction: a connected client uploads a raster of the canvas state up to
    // GlobalId. The server flattens events <= GlobalId into the image and removes
    // them, keeping the event log bounded. Open to anyone who can draw in the room
    // (same trust model as drawing); range-validated server side.
    [HttpPost("{roomId:int}/snapshot")]
    public async Task<IActionResult> UploadSnapshot(
        int roomId,
        [FromBody] SnapshotRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return BadRequest();
        }

        if (!await _rooms.RoomExistsAsync(roomId, cancellationToken))
        {
            return NotFound();
        }

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(RemoveDataUriPrefix(request.ImageBase64));
        }
        catch (FormatException)
        {
            return BadRequest("ImageBase64 is not valid base64.");
        }

        if (imageBytes.Length == 0 || imageBytes.Length > 16 * 1024 * 1024)
        {
            return BadRequest("Snapshot image size out of range.");
        }

        var snapshotDir = Path.Combine(_environment.WebRootPath, "uploads", "snapshots");
        Directory.CreateDirectory(snapshotDir);

        // globalId-stamped name so a rejected (stale) upload never clobbers the
        // currently referenced snapshot file.
        var fileName = $"room-{roomId}-{request.GlobalId}.png";
        var filePath = Path.Combine(snapshotDir, fileName);
        await System.IO.File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken);

        var webPath = $"/uploads/snapshots/{fileName}";
        var (previousPath, _) = await _rooms.GetRoomSnapshotAsync(roomId, cancellationToken);
        var applied = await _roomConnections.ApplySnapshotAsync(roomId.ToString(), webPath, request.GlobalId, cancellationToken);

        if (applied)
        {
            // Remove the now-orphaned previous snapshot file (best-effort).
            if (!string.IsNullOrEmpty(previousPath) && previousPath != webPath)
            {
                TryDeletePhysicalFile(previousPath);
            }
        }
        else
        {
            // Stale/out-of-range: drop the file we just wrote so it doesn't linger.
            TryDeletePhysicalFile(webPath);
        }

        return Ok(new { applied });
    }

    // Clears the whole canvas for everyone in the room. Open to anyone who can draw
    // in the room (same trust model as drawing and snapshot upload), so it also works
    // for guests and non-owners — e.g. the shared default/test room.
    [HttpPost("{roomId:int}/clear")]
    public async Task<IActionResult> ClearRoom(int roomId, CancellationToken cancellationToken)
    {
        if (!await _rooms.RoomExistsAsync(roomId, cancellationToken))
        {
            return NotFound();
        }

        await _roomConnections.ClearRoomAsync(roomId.ToString(), cancellationToken);
        TryDeleteRoomSnapshotFiles(roomId);
        return Ok();
    }

    private void TryDeletePhysicalFile(string webPath)
    {
        try
        {
            var relative = webPath.Split('?')[0].TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var physical = Path.Combine(_environment.WebRootPath, relative);
            if (System.IO.File.Exists(physical))
            {
                System.IO.File.Delete(physical);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private void TryDeleteRoomSnapshotFiles(int roomId)
    {
        try
        {
            var snapshotDir = Path.Combine(_environment.WebRootPath, "uploads", "snapshots");
            if (!Directory.Exists(snapshotDir))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(snapshotDir, $"room-{roomId}-*.png"))
            {
                System.IO.File.Delete(file);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static string RemoveDataUriPrefix(string value)
    {
        var commaIndex = value.IndexOf(',');
        return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0
            ? value[(commaIndex + 1)..]
            : value;
    }
}
