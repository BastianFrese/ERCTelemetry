using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using F1Game.UDP.Enums;
using ERCTelemetry.Core.Persistence;
using ERCTelemetry.Core.Session;

namespace ERCTelemetry.App.Dashboard;

/// <summary>TrackTitan-style session-card list for the History tab: filters (track,
/// session type, time range), paged card grid backed by the DB query, and the
/// list ↔ detail navigation. The detail pipeline itself stays in the main partial.</summary>
public sealed partial class HistoryViewModel
{
    /// <summary>Sentinel entries heading the filter lists — select them to clear the
    /// corresponding filter axis.</summary>
    private const string AllTracks = "Alle Strecken";
    private const string AllSessionTypes = "Alle Session-Typen";

    private const int CardsPerPage = 12; // 2 columns × 6 rows

    private string _filterTrack = AllTracks;
    private string _filterSessionType = AllSessionTypes;
    private int _filterRangeIndex;
    private int _pageIndex;
    private int _pageCount = 1;
    private bool _isDetailOpen;

    /// <summary>Raised after a card opens the detail view — the host scrolls to top.</summary>
    public event Action? DetailOpened;

    /// <summary>Raised after "Zurück" returns to the card list.</summary>
    public event Action? ListOpened;

    /// <summary>One row per card; presentation (image/monogram/team color) is resolved
    /// once here so the XAML stays dumb.</summary>
    public sealed record SessionCardRow(
        long Id, ulong SessionUid, string Track, string SessionType,
        string Badge, string? ImageUri, string Monogram, Brush TileBackground,
        string Team, Brush TeamBrush, string BestLap, int LapCount, int DriverCount,
        string DateText, int Finalized, int Position);

    public ObservableCollection<SessionCardRow> Cards { get; } = [];

    public ObservableCollection<string> TrackOptions { get; } = [];

    public ObservableCollection<string> SessionTypeOptions { get; } = [];

    /// <summary>Numbered pagination items (1-based) with the current-page flag for the
    /// XAML highlight trigger.</summary>
    public sealed record PageItem(int Number, bool IsCurrent);

    public ObservableCollection<PageItem> PageItems { get; } = [];

    /// <summary>Selected track filter (sentinel = no filter).</summary>
    public string FilterTrack
    {
        get => _filterTrack;
        set { if (SetField(ref _filterTrack, value)) { _pageIndex = 0; ApplyFilters(); } }
    }

    /// <summary>Selected session-type filter (sentinel = no filter).</summary>
    public string FilterSessionType
    {
        get => _filterSessionType;
        set { if (SetField(ref _filterSessionType, value)) { _pageIndex = 0; ApplyFilters(); } }
    }

    /// <summary>0 = Gesamt, 1 = 7 Tage, 2 = 30 Tage.</summary>
    public int FilterRangeIndex
    {
        get => _filterRangeIndex;
        set { if (SetField(ref _filterRangeIndex, value)) { _pageIndex = 0; ApplyFilters(); } }
    }

    /// <summary>0-based page; clamped against <see cref="PageCount"/>.</summary>
    public int PageIndex
    {
        get => _pageIndex;
        private set => SetField(ref _pageIndex, value);
    }

    /// <summary>Pages under the current filter, always >= 1.</summary>
    public int PageCount
    {
        get => _pageCount;
        private set => SetField(ref _pageCount, value);
    }

    /// <summary>Pagination caption: "Seite 1 von 2 · 18 Sessions".</summary>
    public string PageLabel { get; private set; } = "Seite 1 von 1";

    public bool CanGoBack => PageIndex > 0;

    public bool CanGoForward => PageIndex < PageCount - 1;

    /// <summary>true while a session detail is open on top of the card list.</summary>
    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        private set => SetField(ref _isDetailOpen, value);
    }

    /// <summary>Rebuilds filter option lists from the DB (DISTINCT + sentinel), keeping
    /// the current selection when still present. Called from <see cref="Refresh"/> —
    /// does not re-query the cards itself.</summary>
    private void RefreshFilterOptions()
    {
        RefillOptionList(TrackOptions, AllTracks, _db.GetDistinctTracks(), ref _filterTrack);
        RefillOptionList(
            SessionTypeOptions, AllSessionTypes, _db.GetDistinctSessionTypes(),
            ref _filterSessionType);
    }

    private static void RefillOptionList(
        ObservableCollection<string> list, string sentinel,
        IReadOnlyList<string> values, ref string current)
    {
        list.Clear();
        list.Add(sentinel);
        foreach (var v in values)
        {
            list.Add(v);
        }

        if (current != sentinel && !list.Contains(current))
        {
            current = sentinel;
        }
    }

    /// <summary>Re-queries the card grid with the current filters and page. Clamps the
    /// page when the total shrinks and refreshes the pagination controls.</summary>
    public void ApplyFilters()
    {
        var page = QueryCards(_pageIndex);
        PageCount = Math.Max(1, (page.TotalCount + CardsPerPage - 1) / CardsPerPage);
        if (_pageIndex >= PageCount)
        {
            PageIndex = PageCount - 1;
            // One re-query at most: totals only shrink when rows were deleted.
            page = QueryCards(PageIndex);
        }

        Cards.Clear();
        foreach (var s in page.Items)
        {
            Cards.Add(ToCardRow(s));
        }

        PageLabel = $"Seite {PageIndex + 1} von {PageCount} · {page.TotalCount} Sessions";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanGoBack)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanGoForward)));

        PageItems.Clear();
        for (var p = 1; p <= PageCount; p++)
        {
            PageItems.Add(new PageItem(p, p == PageIndex + 1));
        }
    }

    private TelemetryDb.SessionCardPage QueryCards(int page)
    {
        var fromUtc = _filterRangeIndex switch
        {
            1 => DateTime.UtcNow.AddDays(-7),
            2 => DateTime.UtcNow.AddDays(-30),
            _ => (DateTime?)null,
        };

        return _db.GetSessionCards(new TelemetryDb.SessionCardFilter(
            Track: FilterTrack == AllTracks ? null : FilterTrack,
            SessionType: FilterSessionType == AllSessionTypes ? null : FilterSessionType,
            StartedFromUtc: fromUtc,
            Page: page,
            PageSize: CardsPerPage));
    }

    private static SessionCardRow ToCardRow(TelemetryDb.SessionCard s) => new(
        s.Id, s.SessionUid, TrackImage.Label(s.Track), s.SessionType,
        TrackImage.SessionTypeBadge(s.SessionType),
        TrackImage.ImageUri(s.Track),
        TrackImage.Monogram(s.Track),
        TrackImage.MonogramBackground(s.Track),
        TeamName.Display(s.PlayerTeam),
        TeamColors.For(Enum.TryParse<Team>(s.PlayerTeam, out var team)
            ? team : default),
        s.PlayerBestLapMs > 0 ? FormatMs(s.PlayerBestLapMs) : "—",
        s.LapCount, s.DriverCount, FormatUtc(s.StartedUtc), s.Finalized, s.PlayerPosition);

    public void PreviousPage()
    {
        if (CanGoBack)
        {
            PageIndex--;
            ApplyFilters();
        }
    }

    public void NextPage()
    {
        if (CanGoForward)
        {
            PageIndex++;
            ApplyFilters();
        }
    }

    /// <summary>Numbered pagination button: page parameter is 1-based.</summary>
    public void GoToPage(int page)
    {
        var target = Math.Clamp(page - 1, 0, PageCount - 1);
        if (target != PageIndex)
        {
            PageIndex = target;
            ApplyFilters();
        }
    }

    /// <summary>Opens a card in the detail view: builds the existing <see cref="SessionRow"/>
    /// so the whole detail pipeline (results, laps, pace, events, comparison) runs
    /// unchanged. Filter and page state survive.</summary>
    public void OpenSession(SessionCardRow card)
    {
        var row = new SessionRow(
            card.Id, card.SessionUid, $"{card.SessionType} · {card.Track}",
            card.DateText, LabelFinalized(card.Finalized), card.LapCount,
            card.DriverCount, card.Track);
        if (SelectedSession == row)
        {
            LoadSession(); // record-equal selection skips the setter — reload explicitly
        }
        else
        {
            SelectedSession = row;
        }

        IsDetailOpen = true;
        DetailOpened?.Invoke();
    }

    /// <summary>Returns to the card list; the selected session stays for the compare picker.</summary>
    public void CloseDetail()
    {
        IsDetailOpen = false;
        ListOpened?.Invoke();
    }
}