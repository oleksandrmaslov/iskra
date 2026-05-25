namespace FlashlightApp.Core;

public enum FlashResult { Pass, Fail }

public sealed record FlashOutcome(
    FlashResult Result,
    string? ErrorCode,
    string? ErrorMessage,
    string? DetectedTarget,
    TimeSpan Duration,
    string GdbTail)
{
    public bool IsPass => Result == FlashResult.Pass;
}
