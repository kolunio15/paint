namespace Paint.Models;

public class Comment
{
    public int Id { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; }

    public int ArtworkId { get; set; }
    public Artwork Artwork { get; set; } = null!;

    public required string AuthorId { get; set; }
    public ApplicationUser Author { get; set; } = null!;
}
