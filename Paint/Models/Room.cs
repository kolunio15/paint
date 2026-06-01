namespace Paint.Models;

public class Room
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int MaxUsers { get; set; } = 10;
    public int CanvasWidth { get; set; } = PaintDefaults.CanvasWidth;
    public int CanvasHeight { get; set; } = PaintDefaults.CanvasHeight;
    public bool IsProtected { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime CreatedAt { get; set; }

    public required string OwnerId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;

    public ICollection<RoomParticipant> Participants { get; set; } = [];
    public ICollection<CanvasEvent> CanvasEvents { get; set; } = [];
}
