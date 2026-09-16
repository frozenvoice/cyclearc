namespace CycleArc.Updates;

public sealed record UpdateRecoveryResult(bool Succeeded, bool Restored, Exception? Failure);

/// <summary>Retains a verified external backup through replacement and desktop readiness.</summary>
public static class UpdateRecoveryTransaction
{
    public static UpdateRecoveryResult Run(UpdateRecoverySnapshot snapshot, Action apply,
        Action startAndVerify, Action stopFailed, Action startAndVerifyPrevious)
    {
        // A damaged backup must prevent replacement, rather than fail only when needed.
        snapshot.Verify();
        try
        {
            apply();
            startAndVerify();
            return new(true, false, null);
        }
        catch (Exception updateFailure)
        {
            try
            {
                stopFailed();
                snapshot.Restore();
                startAndVerifyPrevious();
                return new(false, true, updateFailure);
            }
            catch (Exception recoveryFailure)
            {
                return new(false, false, new AggregateException(updateFailure, recoveryFailure));
            }
        }
    }
}
