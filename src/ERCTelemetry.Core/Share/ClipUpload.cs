using System.Collections.Concurrent;

namespace ERCTelemetry.Core.Share;

/// <summary>Chunked clip upload protocol. The app splits clips larger than one request can
/// safely carry (the edge server in front of the share server caps request bodies at
/// 100 MB) into <see cref="MaxPartBytes"/>-sized parts; each part is its own PUT
/// (?part=N&amp;parts=M) to the same clip URL, and the server assembles the parts into the
/// final MP4 when the last one arrives. Constants and partition math live here so the app
/// and the share server stay in sync.</summary>
public static class ClipUpload
{
    /// <summary>Query parameter carrying the zero-based part index being uploaded.</summary>
    public const string PartQuery = "part";

    /// <summary>Query parameter carrying the total number of parts.</summary>
    public const string PartsQuery = "parts";

    // A part must stay well under Cloudflare Free's 100 MB request-body cap; 80 MB leaves
    // headroom for headers and a comfortable margin, while remaining far below Kestrel's
    // 200 MB per-request limit on the share server.
    public const long MaxPartBytes = 80L * 1024 * 1024;

    /// <summary>Upper bound for the part count, so a malformed request can never force a
    /// long assembly loop. (4096 parts would allow a single clip up to 320 GB.)</summary>
    public const int MaxParts = 4096;

    /// <summary>Upper bound for the total bytes one session may accumulate across all its
    /// clip-upload requests (chunked parts included). Sits next to the Kestrel per-request
    /// cap: a per-request limit alone would let an attacker write 4096 × 80 MB of parts
    /// into one session before any check trips. 2 GiB is far above what a real race
    /// produces (~1.5 GiB at 4K) and low enough to bound disk use.</summary>
    public const long MaxSessionBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Upper bound for the total bytes ALL sessions may collectively accumulate.
    /// The per-session budget alone does not bound disk use: a holder of the public upload
    /// token can open unlimited sessions, each with room for its own
    /// <see cref="MaxSessionBytes"/>. 20 GiB caps the whole server while remaining far above
    /// a league's real storage.</summary>
    public const long MaxGlobalBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>How many parts a file of <paramref name="totalBytes"/> splits into. Files
    /// up to <see cref="MaxPartBytes"/> stay a single request.</summary>
    public static int PartCount(long totalBytes) => totalBytes <= MaxPartBytes
        ? 1
        : (int)((totalBytes + MaxPartBytes - 1) / MaxPartBytes);

    /// <summary>Start offset (in the file) of part <paramref name="part"/>.</summary>
    public static long PartOffset(int part, long totalBytes) => part * MaxPartBytes;

    /// <summary>Byte count of part <paramref name="part"/> — every part except the last is
    /// <see cref="MaxPartBytes"/>, the last is shortened to the remainder.</summary>
    public static long PartLength(int part, long totalBytes) =>
        Math.Min(MaxPartBytes, totalBytes - PartOffset(part, totalBytes));

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/> while
    /// counting the bytes written, returning <c>false</c> as soon as the total would exceed
    /// <paramref name="maxBytes"/>. On a completed, within-budget copy returns <c>true</c>
    /// together with the exact byte count. Counting the bytes that were actually read — not
    /// a declared Content-Length — is what bounds the disk a chunked upload can occupy: a
    /// client can send a body without a content-length (or with a lying small one) whose
    /// real size is unbounded, and only counting as we copy catches that. On <c>false</c>
    /// parts of <paramref name="destination"/> may already hold data; the caller decides
    /// whether to keep or delete them.</summary>
    public static async Task<(bool Ok, long Bytes)> CopyWithinBudgetAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken ct = default)
    {
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return (true, written);
            }

            written += read;
            if (written > maxBytes)
            {
                return (false, written);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Claims <paramref name="add"/> bytes against a session's <paramref name="maxBytes"/>
    /// budget, returning <c>true</c> only when the claim was applied without exceeding it. The
    /// accounting is a compare-and-swap on the <paramref name="sessionBytes"/> entry, so concurrent
    /// part uploads of the same session cannot collectively overshoot the cap even though each
    /// request checked its own claim independently.</summary>
    public static bool TryClaimSessionBytes(
        ConcurrentDictionary<ulong, long> sessionBytes, ulong sessionUid, long add, long maxBytes)
    {
        while (true)
        {
            if (!sessionBytes.TryGetValue(sessionUid, out var used))
            {
                // No entry yet: only insert a claim that fits on its own, so a single
                // oversized claim cannot create an over-budget entry. A lost TryAdd race
                // against a concurrent writer just loops back and re-checks.
                if (add > maxBytes)
                {
                    return false;
                }

                if (sessionBytes.TryAdd(sessionUid, add))
                {
                    return true;
                }
                continue;
            }

            if (used > maxBytes || add > maxBytes - used)
            {
                return false;
            }
            if (sessionBytes.TryUpdate(sessionUid, used + add, used))
            {
                return true;
            }
        }
    }

    /// <summary>Claims <paramref name="add"/> bytes against the server-global budget
    /// (<see cref="MaxGlobalBytes"/>), the same compare-and-swap pattern as
    /// <see cref="TryClaimSessionBytes"/> but on the single shared counter — concurrent
    /// uploads across all sessions cannot collectively exceed the cap. The bytes are returned
    /// to the budget with <see cref="ReleaseGlobalBytes"/> when a session is deleted or
    /// purged.</summary>
    public static bool TryClaimGlobalBytes(ref long total, long add, long maxGlobalBytes)
    {
        while (true)
        {
            var current = Volatile.Read(ref total);
            // Overflow-guarded: `current + add` could wrap negative for operator-set caps
            // near long.MaxValue. Rewritten as subtraction (safe while current ≤ cap).
            if (current > maxGlobalBytes || add > maxGlobalBytes - current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref total, current + add, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Returns <paramref name="refundBytes"/> of a session's claimed accounting to
    /// the caller, returning the number of bytes actually released from the session entry
    /// (0 when the session holds no releasable claim). The retention sweep uses this when it
    /// reaps an orphaned part file: the part's bytes were counted when uploaded but no longer
    /// occupy disk, so the per-session (and by the caller, the server-global) budget must
    /// shrink back. Same compare-and-swap discipline as <see cref="TryClaimSessionBytes"/> — a
    /// concurrent claim cannot break the accounting. If the refund covers the whole entry the
    /// entry is removed (a re-created session starts clean at 0) and the returned amount is
    /// the entry's full value — never more than it held. A session with no entry — e.g. a
    /// DELETE already released it, or a session reset to 0 by a POST re-create whose stale
    /// files the sweep later reaps — returns 0, so the caller must not release anything from
    /// the global counter either, or the release would be double-counted.</summary>
    public static long TryReleaseSessionBytes(
        ConcurrentDictionary<ulong, long> sessionBytes, ulong sessionUid, long refundBytes)
    {
        if (refundBytes <= 0)
        {
            return 0;
        }

        while (true)
        {
            if (!sessionBytes.TryGetValue(sessionUid, out var claimed))
            {
                return 0;
            }

            var remaining = claimed - refundBytes;
            if (remaining <= 0)
            {
                // The refund covers the whole entry; a value-conditional remove keeps a
                // concurrent claim to this session from being swept away with the entry.
                // Only the entry's own value is released — never the caller's refundBytes,
                // which can exceed it after a reconcile reset the entry to 0 while the old
                // files were still waiting to be reaped.
                if (sessionBytes.TryRemove(new KeyValuePair<ulong, long>(sessionUid, claimed)))
                {
                    return claimed;
                }
                continue; // lost the race, re-read
            }

            if (sessionBytes.TryUpdate(sessionUid, remaining, claimed))
            {
                return refundBytes;
            }
        }
    }

    /// <summary>Returns <paramref name="bytes"/> to the global budget after a session that
    /// claimed them was deleted or purged.</summary>
    public static void ReleaseGlobalBytes(ref long total, long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref total, -bytes);
        }
    }
}
