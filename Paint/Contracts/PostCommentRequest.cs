using System.ComponentModel.DataAnnotations;

namespace Paint.Contracts;

public sealed class PostCommentRequest
{
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Message { get; set; } = "";
}
