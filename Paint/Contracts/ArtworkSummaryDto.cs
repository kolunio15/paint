namespace Paint.Contracts;

public sealed record ArtworkSummaryDto(
    int Id,
    string Title,
    string ThumbnailUrl,
    int Score,
    bool IsHidden = false);
