using System.ComponentModel.DataAnnotations;

namespace Paint.Contracts;

public sealed class AddRoomEventRequest
{
    public int? GlobalEventId { get; set; }

    [Required]
    public string Content { get; set; } = "{}";
}
