namespace Paint.Contracts;

public sealed record ArtworkDetailsDto(
    long Id,
    string Title,
    string ImageUrl,
    IReadOnlyList<UserDto> Users,
    IReadOnlyList<CommentDto> Comments,
    int Score);
