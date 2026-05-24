using System.ComponentModel.DataAnnotations;

namespace Paint.Models;

public class CanvasEvent
{
    public long Id { get; set; }

    [Required]
    [MaxLength(128)]
    public string RoomId { get; set; } = "";

    public PaintRoom? Room { get; set; }

    [Required]
    [MaxLength(64)]
    public string EventType { get; set; } = "";

    public int? ClientGlobalEventId { get; set; }

    public string PayloadJson { get; set; } = "{}";

    [MaxLength(450)]
    public string? UserId { get; set; }

    [MaxLength(256)]
    public string? UserName { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
