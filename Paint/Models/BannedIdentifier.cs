namespace Paint.Models;

public enum BannedIdentifierType
{
    Ip,
    DeviceToken
}

public class BannedIdentifier
{
    public int Id { get; set; }
    public BannedIdentifierType Type { get; set; }
    public required string ValueHash { get; set; }

    public string? OriginalUserId { get; set; }
    public ApplicationUser? OriginalUser { get; set; }

    public required string BannedById { get; set; }
    public ApplicationUser BannedBy { get; set; } = null!;

    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}
