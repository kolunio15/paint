using Microsoft.AspNetCore.Identity;

namespace Paint.Models;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }

    public ICollection<Artwork> Artworks { get; set; } = [];
    public ICollection<Comment> Comments { get; set; } = [];
    public ICollection<Vote> Votes { get; set; } = [];
    public ICollection<RoomParticipant> RoomParticipants { get; set; } = [];
    public ICollection<Room> OwnedRooms { get; set; } = [];
    public ICollection<CanvasEvent> CanvasEvents { get; set; } = [];
}
