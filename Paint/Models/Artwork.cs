namespace Paint.Models;

public class Artwork
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string ImageUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsHidden { get; set; }
    public DateTime? HiddenAt { get; set; }
    public string? HiddenById { get; set; }
    public DateTime? ScheduledDeletionAt { get; set; }

    public required string AuthorId { get; set; }
    public ApplicationUser Author { get; set; } = null!;

    public ICollection<Comment> Comments { get; set; } = [];
    public ICollection<Vote> Votes { get; set; } = [];
}
