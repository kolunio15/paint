using System.ComponentModel.DataAnnotations;

namespace Paint.Models;

public class ArtworkComment
{
    public long Id { get; set; }

    public long ArtworkId { get; set; }

    public Artwork? Artwork { get; set; }

    [MaxLength(450)]
    public string? UserId { get; set; }

    [MaxLength(256)]
    public string UserName { get; set; } = "Unknown";

    [Required]
    [MaxLength(2000)]
    public string Content { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
