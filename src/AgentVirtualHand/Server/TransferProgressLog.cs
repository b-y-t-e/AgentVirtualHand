using Tailcat.Link;

namespace AgentVirtualHand.Server;

/// <summary>
/// Postep transferu w logu co 10% - widac, ze duzy plik posuwa sie mimo rozlaczen (wznawianie dziala).
/// Wspolny dla hosta i huba, zeby oba logi mialy ten sam prog i format.
/// </summary>
public static class TransferProgressLog
{
    private const int Buckets = 10;

    public static IProgress<TransferProgress> Create(string label, Action<string> write)
    {
        var lastBucket = -1;
        return new Progress<TransferProgress>(p =>
        {
            if (p.Fraction is not { } fraction) return;
            var bucket = Math.Clamp((int)(fraction * Buckets), 0, Buckets);
            if (bucket <= lastBucket) return;
            lastBucket = bucket;
            write($"{label} {bucket * 100 / Buckets}%");
        });
    }
}
