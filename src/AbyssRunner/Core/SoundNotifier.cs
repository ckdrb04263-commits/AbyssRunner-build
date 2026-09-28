namespace AbyssRunner.Core;

public static class SoundNotifier
{
    public static void CountdownTick() => System.Media.SystemSounds.Asterisk.Play();
    public static void Success() => System.Media.SystemSounds.Exclamation.Play();

    public static async Task ErrorForThreeSecondsAsync(CancellationToken ct = default)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            System.Media.SystemSounds.Hand.Play();
            try { await Task.Delay(420, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}
