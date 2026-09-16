using System.Collections.ObjectModel;
using System.ComponentModel;
using ERCTelemetry.App.Composition;
using ERCTelemetry.App.Share;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Drives the Setups tab: shows the Discord login state, loads the track setups
/// the logged-in user may view from the website (<c>GET /api/setups</c>), filters them by
/// track/game year client-side and turns a selected setup into an ERC1 share code the
/// player can paste into F1 26. The Discord token comes from <see cref="DiscordLoginHost"/>
/// (RAM only).</summary>
public sealed class SetupsViewModel : INotifyPropertyChanged
{
    private readonly DiscordLoginHost _discord;
    private readonly ErcSetupsClient _client;
    private string _status = "Nicht eingeloggt";
    private string _userText = "Nicht eingeloggt";
    private string _selectedTrack = string.Empty;
    private string _selectedGameYear = string.Empty;
    private ErcSetup? _selectedSetup;
    private string _erc1Code = string.Empty;
    private string _loadStatus = string.Empty;
    private IReadOnlyList<ErcSetup> _all = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public SetupsViewModel(DiscordLoginHost discord, ErcSetupsClient client)
    {
        _discord = discord;
        _client = client;
        _discord.StatusChanged += RefreshLoginState;
        _discord.UserChanged += RefreshLoginState;
        RefreshLoginState();
    }

    /// <summary>Login status line (mirrors the host's status).</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    /// <summary>Who is logged in, e.g. "Eingeloggt als Max" / "Nicht eingeloggt".</summary>
    public string UserText
    {
        get => _userText;
        private set
        {
            if (_userText == value)
            {
                return;
            }

            _userText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserText)));
        }
    }

    /// <summary>True while a Discord user is logged in (the setups API needs the token).</summary>
    public bool IsLoggedIn => _discord.User is not null;

    /// <summary>All tracks present in the loaded setups (filter dropdown).</summary>
    public ObservableCollection<string> Tracks { get; } = [];

    /// <summary>All game years present in the loaded setups (filter dropdown).</summary>
    public ObservableCollection<string> GameYears { get; } = [];

    /// <summary>The setups matching the current track/year filter.</summary>
    public ObservableCollection<ErcSetup> Setups { get; } = [];

    public string SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (_selectedTrack == value)
            {
                return;
            }

            _selectedTrack = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTrack)));
            ApplyFilter();
        }
    }

    public string SelectedGameYear
    {
        get => _selectedGameYear;
        set
        {
            if (_selectedGameYear == value)
            {
                return;
            }

            _selectedGameYear = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGameYear)));
            ApplyFilter();
        }
    }

    public ErcSetup? SelectedSetup
    {
        get => _selectedSetup;
        set
        {
            if (ReferenceEquals(_selectedSetup, value))
            {
                return;
            }

            _selectedSetup = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSetup)));
            UpdateCode();
        }
    }

    /// <summary>ERC1 share code of the selected setup (empty until one is selected).</summary>
    public string Erc1Code
    {
        get => _erc1Code;
        private set
        {
            if (_erc1Code == value)
            {
                return;
            }

            _erc1Code = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Erc1Code)));
        }
    }

    /// <summary>Load status of the last fetch (e.g. "12 Setups geladen").</summary>
    public string LoadStatus
    {
        get => _loadStatus;
        private set
        {
            if (_loadStatus == value)
            {
                return;
            }

            _loadStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LoadStatus)));
        }
    }

    /// <summary>Re-reads the login state from the host (login/logout events).</summary>
    private void RefreshLoginState()
    {
        UserText = _discord.User is { } u
            ? $"Eingeloggt als {u.GlobalName ?? u.Username}"
            : "Nicht eingeloggt";
        Status = _discord.Status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoggedIn)));
        if (!IsLoggedIn)
        {
            _all = [];
            Tracks.Clear();
            GameYears.Clear();
            Setups.Clear();
            SelectedSetup = null;
            Erc1Code = string.Empty;
            LoadStatus = string.Empty;
        }
    }

    /// <summary>Loads the setups for the logged-in user (all tracks/years), then applies the
    /// current filter. Clears the list when not logged in.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!IsLoggedIn)
        {
            LoadStatus = "Bitte zuerst mit Discord einloggen.";
            return;
        }

        LoadStatus = "Lade Setups …";
        var response = await _client.GetSetupsAsync(_discord.AccessToken, ct: ct);
        if (response is null)
        {
            LoadStatus = "Setups konnten nicht geladen werden — Login prüfen oder erneut einloggen.";
            return;
        }

        _all = response.Setups;
        Tracks.Clear();
        GameYears.Clear();
        foreach (var track in _all.Select(s => s.Track).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            Tracks.Add(track);
        }

        foreach (var year in _all.Select(s => s.GameYear).Where(y => !string.IsNullOrWhiteSpace(y))
                     .Select(y => y!).Distinct().OrderByDescending(y => y))
        {
            GameYears.Add(year);
        }

        ApplyFilter();
        LoadStatus = _all.Count == 0
            ? "Keine Setups für deinen Zugriff verfügbar."
            : $"{_all.Count} Setups geladen.";
    }

    /// <summary>Re-applies the track/year filter to the loaded setups.</summary>
    private void ApplyFilter()
    {
        var filtered = _all.Where(s =>
            (string.IsNullOrWhiteSpace(SelectedTrack) || s.Track == SelectedTrack) &&
            (string.IsNullOrWhiteSpace(SelectedGameYear) || s.GameYear == SelectedGameYear)).ToList();

        Setups.Clear();
        foreach (var setup in filtered)
        {
            Setups.Add(setup);
        }

        // A filtered-out selection must not keep showing a stale code.
        if (SelectedSetup is not null && !filtered.Contains(SelectedSetup))
        {
            SelectedSetup = null;
        }
    }

    /// <summary>Maps the selected setup's website payload to a <see cref="CarSetupSnapshot"/>
    /// and encodes it as an ERC1 share code (the guaranteed in-game import path).</summary>
    private void UpdateCode()
    {
        if (SelectedSetup is null)
        {
            Erc1Code = string.Empty;
            return;
        }

        var snapshot = ErcSetupMapper.MapToCarSetupSnapshot(SelectedSetup.PayloadJson);
        Erc1Code = SetupCodec.Encode(snapshot);
    }
}
