using System.Windows;
using ERCTelemetry.App.Composition;
using ERCTelemetry.App.Share;
using ERCTelemetry.Core.Share;

namespace ERCTelemetry.App.Dashboard;

/// <summary>Modal „Ergebnis an erdi-erc.de senden?" prompt after a finished league race.
/// Shows the race facts, lets the driver pick the league from <c>GET /api/telemetry/leagues</c>
/// (only leagues the key owner is an active member of) and sends the final classification via
/// <see cref="ErcRaceSender"/>. "Später" keeps the result local — the admin never sees it.</summary>
public partial class ErcSendRaceDialog : Window
{
    private readonly ErcRaceEndedEvent _raceEnded;
    private readonly ErcRaceSender _sender;

    public ErcSendRaceDialog(ErcRaceEndedEvent raceEnded, ErcRaceSender sender)
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
        _raceEnded = raceEnded;
        _sender = sender;

        TrackText.Text = raceEnded.Track;
        DateText.Text = raceEnded.StartedUtc.ToLocalTime().ToString("g");
        InfoText.Text =
            $"Fahrer: {raceEnded.Finishes.Count} · Schnellste Runde: {raceEnded.FastestLap ?? "—"}";
        StatusText.Text = "Lade Ligen …";

        Loaded += async (_, _) => await LoadLeaguesAsync();
    }

    /// <summary>Fills the league dropdown from the ERC endpoint; the send button stays
    /// disabled while no league is selected (or the load failed).</summary>
    private async Task LoadLeaguesAsync()
    {
        var leagues = await _sender.GetLeaguesAsync();
        LeagueBox.ItemsSource = leagues;
        LeagueBox.DisplayMemberPath = nameof(ErcLeague.Name);
        LeagueBox.SelectedIndex = leagues.Count == 1 ? 0 : -1;

        StatusText.Text = leagues.Count == 0
            ? "Keine Ligen ladbar — passt der Key? (Einstellungen → ERC-Ergebnis → „Verbindung testen“)"
            : "Liga wählen, dann senden.";
    }

    private void LeagueBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SendButton.IsEnabled = LeagueBox.SelectedItem is ErcLeague;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (LeagueBox.SelectedItem is not ErcLeague league)
        {
            return;
        }

        SendButton.IsEnabled = false;
        StatusText.Text = "Sende …";
        var payload = new ErcRaceResultPayload(
            Track: _raceEnded.Track,
            Date: _raceEnded.StartedUtc,
            League: league.Name,
            Season: null,
            FastestLap: _raceEnded.FastestLap,
            Finishes: _raceEnded.Finishes);

        var result = await _sender.SendAsync(payload);
        if (result.Success)
        {
            StatusText.Text = $"Gesendet — Entwurf #{result.PendingId} liegt beim Admin zur Freigabe.";
        }
        else
        {
            StatusText.Text = result.Error ?? "Senden fehlgeschlagen.";
            SendButton.IsEnabled = true;
        }
    }
}
