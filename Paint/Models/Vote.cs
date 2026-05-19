namespace Paint.Models;

public class Vote
{
    public int Id { get; set; }
    public int Value { get; set; } // +1 lub -1

    public int ArtworkId { get; set; }
    public Artwork Artwork { get; set; } = null!;

    public required string UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
}
