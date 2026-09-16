using System.Collections.Concurrent;
using ERCTelemetry.Core.Share;
using Xunit;

namespace ERCTelemetry.Core.Tests.Share;

/// <summary>Partition math of the chunked clip upload protocol (app ↔ share server).</summary>
public sealed class ClipUploadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ClipUpload.MaxPartBytes)]
    public void Files_up_to_max_part_stay_a_single_request(long bytes) =>
        Assert.Equal(1, ClipUpload.PartCount(bytes));

    [Fact]
    public void One_byte_over_max_part_splits_into_two_parts() =>
        Assert.Equal(2, ClipUpload.PartCount(ClipUpload.MaxPartBytes + 1));

    [Fact]
    public void Exactly_double_splits_into_two_parts() =>
        Assert.Equal(2, ClipUpload.PartCount(ClipUpload.MaxPartBytes * 2));

    [Fact]
    public void Just_over_double_splits_into_three_parts() =>
        Assert.Equal(3, ClipUpload.PartCount(ClipUpload.MaxPartBytes * 2 + 1));

    [Theory]
    [InlineData(0, 0L)]
    [InlineData(1, ClipUpload.MaxPartBytes)]
    public void Part_offset_advances_by_max_part(int part, long expected) =>
        Assert.Equal(expected, ClipUpload.PartOffset(part, ClipUpload.MaxPartBytes * 2));

    [Fact]
    public void Only_the_last_part_is_shortened_to_the_remainder()
    {
        var total = ClipUpload.MaxPartBytes * 2 + 123;
        Assert.Equal(ClipUpload.MaxPartBytes, ClipUpload.PartLength(0, total));
        Assert.Equal(ClipUpload.MaxPartBytes, ClipUpload.PartLength(1, total));
        Assert.Equal(123, ClipUpload.PartLength(2, total));
    }

    [Fact]
    public void Part_lengths_cover_the_whole_file_without_gaps_or_overlap()
    {
        var total = ClipUpload.MaxPartBytes + 42;
        var count = ClipUpload.PartCount(total);
        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += ClipUpload.PartLength(i, total);
        }
        Assert.Equal(total, sum);
    }

    [Fact]
    public async Task CopyWithinBudgetAsync_copies_an_exact_budget_stream_fully()
    {
        var payload = new byte[81920];
        payload.AsSpan().Fill(0x11);
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();

        var (ok, bytes) = await ClipUpload.CopyWithinBudgetAsync(source, destination, 81920);

        Assert.True(ok);
        Assert.Equal(payload.Length, bytes);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task CopyWithinBudgetAsync_stops_at_the_cap_leaving_only_the_prefix()
    {
        // One full 81920-byte buffer fits; the next read crosses the cap. The prefix that
        // fit was already written — the caller must delete the partial file on abort.
        var payload = new byte[81920 + 100];
        payload.AsSpan().Fill(0x22);
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();

        var (ok, bytes) = await ClipUpload.CopyWithinBudgetAsync(source, destination, 81920);

        Assert.False(ok);
        Assert.Equal(81920 + 100, bytes);
        Assert.Equal(81920, destination.Length);
        Assert.All(destination.ToArray(), b => Assert.Equal(0x22, b));
    }

    [Fact]
    public async Task TryClaimSessionBytes_accumulates_until_the_cap_then_rejects()
    {
        var dict = new ConcurrentDictionary<ulong, long>();

        Assert.True(ClipUpload.TryClaimSessionBytes(dict, 1, 600, 1000));
        Assert.True(ClipUpload.TryClaimSessionBytes(dict, 1, 400, 1000));
        Assert.False(ClipUpload.TryClaimSessionBytes(dict, 1, 1, 1000)); // 1000 already claimed
        Assert.Equal(1000, dict[1]);

        // Another session has its own, independent, budget.
        Assert.True(ClipUpload.TryClaimSessionBytes(dict, 2, 1000, 1000));
        Assert.False(ClipUpload.TryClaimSessionBytes(dict, 2, 1, 1000));
    }

    [Fact]
    public void A_single_claim_larger_than_the_cap_is_rejected_and_never_inserted()
    {
        var dict = new ConcurrentDictionary<ulong, long>();

        Assert.False(ClipUpload.TryClaimSessionBytes(dict, 3, 2000, 1000));
        Assert.False(dict.ContainsKey(3)); // no over-budget entry may linger
    }

    [Fact]
    public async Task Concurrent_claims_never_exceed_the_cap()
    {
        const ulong session = 7;
        const long max = 100_000;
        var dict = new ConcurrentDictionary<ulong, long>();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                long granted = 0;
                while (ClipUpload.TryClaimSessionBytes(dict, session, 2000, max))
                {
                    granted += 2000;
                }
                return granted;
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.True(dict[session] <= max);
        Assert.Equal(dict[session], tasks.Sum(t => t.Result));
    }

    [Fact]
    public void TryClaimGlobalBytes_accumulates_until_the_cap_then_rejects()
    {
        long total = 0;

        Assert.True(ClipUpload.TryClaimGlobalBytes(ref total, 600, 1000));
        Assert.True(ClipUpload.TryClaimGlobalBytes(ref total, 400, 1000));
        Assert.False(ClipUpload.TryClaimGlobalBytes(ref total, 1, 1000)); // 1000 already claimed
        Assert.Equal(1000, total);
    }

    [Fact]
    public void A_global_claim_larger_than_the_cap_is_rejected_and_changes_nothing()
    {
        // Exactly the shape a single >2 GiB race body would present; the cap must not
        // be exceeded and the counter must stay untouched.
        long total = 0;
        Assert.False(ClipUpload.TryClaimGlobalBytes(ref total, 2000, 1000));
        Assert.Equal(0, total);
    }

    [Fact]
    public void ReleaseGlobalBytes_restores_the_budget_so_later_claims_land()
    {
        long total = 0;
        ClipUpload.TryClaimGlobalBytes(ref total, 3000, 10_000);
        Assert.Equal(3000, total);

        ClipUpload.ReleaseGlobalBytes(ref total, 3000);
        Assert.Equal(0, total);
        // Freed headroom is usable again.
        Assert.True(ClipUpload.TryClaimGlobalBytes(ref total, 10_000, 10_000));
    }

    [Fact]
    public void ReleaseGlobalBytes_ignores_zero_or_negative_bytes()
    {
        long total = 5000;
        ClipUpload.ReleaseGlobalBytes(ref total, 0);
        ClipUpload.ReleaseGlobalBytes(ref total, -100);
        Assert.Equal(5000, total); // never goes negative
    }

    [Fact]
    public void TryReleaseSessionBytes_refunds_a_partial_claim_keeping_the_entry()
    {
        var dict = new ConcurrentDictionary<ulong, long> { [1] = 1000 };

        Assert.Equal(400, ClipUpload.TryReleaseSessionBytes(dict, 1, 400));

        Assert.True(dict.ContainsKey(1));
        Assert.Equal(600, dict[1]); // the remaining claim stays accounted
    }

    [Fact]
    public void TryReleaseSessionBytes_refund_covering_the_whole_entry_removes_it()
    {
        var dict = new ConcurrentDictionary<ulong, long> { [1] = 1000 };

        Assert.Equal(1000, ClipUpload.TryReleaseSessionBytes(dict, 1, 1000));
        Assert.False(dict.ContainsKey(1)); // a re-created session starts clean at 0

        // A refund bigger than the claim returns only what the entry actually held — the
        // sweep can over-report a part's size, but the caller may never release more from
        // the global budget than was claimed for that session.
        var dict2 = new ConcurrentDictionary<ulong, long> { [2] = 1000 };
        Assert.Equal(1000, ClipUpload.TryReleaseSessionBytes(dict2, 2, 5000));
        Assert.False(dict2.ContainsKey(2));
    }

    [Fact]
    public void TryReleaseSessionBytes_on_a_missing_session_returns_zero_and_changes_nothing()
    {
        var dict = new ConcurrentDictionary<ulong, long>();

        // The caller must NOT release anything globally when this returns 0 — a DELETE
        // already released the entry, so the global release for it was already done.
        Assert.Equal(0, ClipUpload.TryReleaseSessionBytes(dict, 1, 100));
        Assert.False(dict.ContainsKey(1));
    }

    [Fact]
    public void TryReleaseSessionBytes_ignores_zero_or_negative_refunds()
    {
        var dict = new ConcurrentDictionary<ulong, long> { [1] = 1000 };

        Assert.Equal(0, ClipUpload.TryReleaseSessionBytes(dict, 1, 0));
        Assert.Equal(0, ClipUpload.TryReleaseSessionBytes(dict, 1, -5));
        Assert.Equal(1000, dict[1]);
    }

    [Fact]
    public void TryReleaseSessionBytes_against_a_session_reset_to_zero_returns_zero()
    {
        // A session re-created by POST is reset to 0 while its old stale parts are still on
        // disk; the reconcile already returned those bytes to the global budget. When the
        // sweep later reaps one of them, nothing further may be released — returning 0 is
        // what tells RefundOrphanPart to skip the global release (double-count guard).
        var dict = new ConcurrentDictionary<ulong, long> { [1] = 0 };

        Assert.Equal(0, ClipUpload.TryReleaseSessionBytes(dict, 1, 500));
    }

    [Fact]
    public async Task Concurrent_refunds_and_claims_keep_the_session_budget_consistent()
    {
        const ulong session = 9;
        const long max = 100_000;
        var dict = new ConcurrentDictionary<ulong, long>();

        // Many threads alternately claim and refund 2000-byte chunks on the same session.
        // The recorded entry must equal the algebraic sum of successful claims minus the
        // amounts actually released (a consistent ledger the CAS loops cannot corrupt) and
        // never drop below zero.
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                long net = 0;
                for (var i = 0; i < 100; i++)
                {
                    if (ClipUpload.TryClaimSessionBytes(dict, session, 2000, max))
                    {
                        net += 2000;
                    }
                    net -= ClipUpload.TryReleaseSessionBytes(dict, session, 2000);
                }
                return net;
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        var final = dict.TryGetValue(session, out var claimed) ? claimed : 0;
        Assert.Equal(final, tasks.Sum(t => t.Result));
        Assert.True(final >= 0);
    }

    [Fact]
    public async Task Concurrent_global_claims_never_exceed_the_cap()
    {
        const long max = 100_000;
        long total = 0;

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                long granted = 0;
                while (ClipUpload.TryClaimGlobalBytes(ref total, 2000, max))
                {
                    granted += 2000;
                }
                return granted;
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.True(Volatile.Read(ref total) <= max);
        Assert.Equal(total, tasks.Sum(t => t.Result));
    }
}
