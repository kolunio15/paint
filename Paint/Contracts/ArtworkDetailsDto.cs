namespace Paint.Contracts;

public sealed record ArtworkDetailsDto(
    int Id,
    string Title,
    string ImageUrl,
    IReadOnlyList<UserDto> Users,
    IReadOnlyList<CommentDto> Comments,
    int Score);
