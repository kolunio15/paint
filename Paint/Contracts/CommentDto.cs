namespace Paint.Contracts;

public sealed record CommentDto(
    int Id,
    UserDto User,
    string Content,
    DateTime CreatedAt);
