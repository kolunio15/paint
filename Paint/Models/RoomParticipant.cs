namespace Paint.Models;

public class RoomParticipant
{
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    public required string UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public DateTime JoinedAt { get; set; }
}
