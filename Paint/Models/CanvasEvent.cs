namespace Paint.Models;

public class CanvasEvent
{
    public int Id { get; set; }
    public required string EventType { get; set; } // np. "stroke", "clear"
    public required string Payload { get; set; }   // JSON z danymi zdarzenia
    public DateTime CreatedAt { get; set; }

    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    public required string UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
}
