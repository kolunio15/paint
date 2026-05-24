using System.ComponentModel.DataAnnotations;

namespace Paint.Models;

public class ArtworkVote
{
    public long ArtworkId { get; set; }

    public Artwork? Artwork { get; set; }

    [MaxLength(450)]
    public string UserId { get; set; } = "";

    public VoteValue Vote { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
