using System.ComponentModel.DataAnnotations;

namespace Paint.Models;

public class Artwork
{
    public long Id { get; set; }

    [Required]
    [MaxLength(120)]
    public string Title { get; set; } = "";

    [Required]
    [MaxLength(300)]
    public string ImageUrl { get; set; } = "";

    [Required]
    [MaxLength(300)]
    public string ThumbnailUrl { get; set; } = "";

    [MaxLength(128)]
    public string? RoomId { get; set; }

    public PaintRoom? Room { get; set; }

    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string? CreatedByUserName { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<ArtworkComment> Comments { get; set; } = [];

    public List<ArtworkVote> Votes { get; set; } = [];
}
