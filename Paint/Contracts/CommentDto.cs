namespace Paint.Contracts;

public sealed record CommentDto(
    UserDto User,
    string Content,
    DateTime CreatedAtUtc);
