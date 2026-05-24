using Paint.Models;

namespace Paint.Contracts;

public sealed class RateArtworkRequest
{
    public VoteValue Vote { get; set; } = VoteValue.Neutral;
}
