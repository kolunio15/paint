namespace Paint.Contracts;

public sealed class RateArtworkRequest
{
    public VoteKind Vote { get; set; } = VoteKind.Neutral;
}
