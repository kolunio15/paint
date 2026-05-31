using System.ComponentModel.DataAnnotations;

namespace Paint.Contracts;

public sealed class AddRoomEventRequest
{
    [Required]
    public string Content { get; set; } = "{}";
}
