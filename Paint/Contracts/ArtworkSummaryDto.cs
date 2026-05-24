namespace Paint.Contracts;

public sealed record ArtworkSummaryDto(
    long Id,
    string Title,
    string ThumbnailUrl);
