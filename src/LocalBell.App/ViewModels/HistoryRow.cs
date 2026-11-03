using System.Globalization;
using LocalBell.Core.History;

namespace LocalBell.App.ViewModels;

/// <summary>The colour of what became of a ring, as on the rows of the list (green a reply, red missed…).</summary>
public enum HistoryTone
{
    Accent,
    Success,
    Neutral,
    Caution,
    Critical,
}

/// <summary>A day of Settings → History: "Aujourd'hui", "Hier", "lundi 2 novembre", and its rings.</summary>
public sealed record HistoryDay(string Title, IReadOnlyList<HistoryRow> Rows);

/// <summary>
/// A ring of Settings → History (the author's choice A, November 3, as a phone's calls): an arrow towards this PC or
/// away from it, "Caisse vous a sonné" (the name in bold), the message, then on the right the time and what came of it.
/// </summary>
public sealed class HistoryRow
{
    public bool Received { get; private init; }

    /// <summary>A ring received that nobody answered: in red, with "Rappeler".</summary>
    public bool Missed { get; private init; }

    public string Before { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string After { get; private init; } = "";
    public string Message { get; private init; } = "";
    public string Time { get; private init; } = "";
    public string Outcome { get; private init; } = "";
    public HistoryTone Tone { get; private init; }

    /// <summary>The PC that rang, rung back by "Rappeler".</summary>
    public string PeerId { get; private init; } = "";

    /// <summary>The rings of the log by day, the newest first.</summary>
    public static IReadOnlyList<HistoryDay> Days(RingLog log)
    {
        var today = DateTime.Today;
        return log.Rings
            .GroupBy(r => r.At.ToLocalTime().Date)
            .Select(day => new HistoryDay(DayTitle(day.Key, today), day.Select(r => From(log, r)).ToList()))
            .ToList();
    }

    private static string DayTitle(DateTime day, DateTime today)
    {
        if (day == today) return Strings.Get("HistoryToday");
        if (day == today.AddDays(-1)) return Strings.Get("HistoryYesterday");
        string text = day.ToString(Strings.Get("HistoryDayFormat"), CultureInfo.CurrentUICulture);
        return text.Length > 0 ? char.ToUpper(text[0], CultureInfo.CurrentUICulture) + text[1..] : text;
    }

    private static HistoryRow From(RingLog log, LoggedRing ring)
    {
        var first = ring.Parties[0];
        // "{0} vous a sonné", "Vous avez sonné {0}": the name between, in bold.
        string line = Strings.Get(ring.Received ? ring.Everyone ? "HistoryRangEveryone" : "HistoryRangYou" : "HistoryYouRang");
        string name = !ring.Received && ring.Everyone ? Strings.Format("HistoryEveryone", ring.Parties.Count) : first.Name;
        string[] around = line.Split("{0}", 2);

        string outcome;
        HistoryTone tone;
        if (ring.Received) (outcome, tone) = Answer(log.Effective(ring, first), first);
        else if (ring.Parties.Count == 1) (outcome, tone) = Sent(log.Effective(ring, first), first, withTime: true);
        else
        {
            // Everyone: "Caisse : 2 min · Labo : Pas de réponse", in the colour they share, else grey.
            var each = ring.Parties.Select(p => (p.Name, Fate: Sent(log.Effective(ring, p), p, withTime: false))).ToList();
            outcome = string.Join(" · ", each.Select(e => Strings.Format("HistoryPart", e.Name, e.Fate.Text)));
            tone = each.All(e => e.Fate.Tone == each[0].Fate.Tone) ? each[0].Fate.Tone : HistoryTone.Neutral;
        }

        var fate = log.Effective(ring, first);
        return new HistoryRow
        {
            Received = ring.Received,
            Missed = ring.Received && fate is RingFate.Unanswered or RingFate.Missed,
            Before = around[0],
            Name = name,
            After = around.Length > 1 ? around[1] : "",
            Message = string.IsNullOrEmpty(ring.Message) ? Strings.Get("HistoryNoMessage") : ring.Message,
            Time = TimeOf(ring.At),
            Outcome = outcome,
            Tone = tone,
            PeerId = first.Id,
        };
    }

    // What this PC did with a ring received.
    private static (string Text, HistoryTone Tone) Answer(RingFate fate, LoggedParty party) => fate switch
    {
        RingFate.Ringing => (Strings.Get("NoteRinging"), HistoryTone.Accent),
        RingFate.Replied => (Strings.Format("HistoryYourReply", party.Reply ?? ""), HistoryTone.Success),
        RingFate.Closed => (Strings.Get("HistoryClosed"), HistoryTone.Neutral),
        _ => (Strings.Get("HistoryMissed"), HistoryTone.Critical),
    };

    // What a PC rung said back.
    private static (string Text, HistoryTone Tone) Sent(RingFate fate, LoggedParty party, bool withTime) => fate switch
    {
        RingFate.Ringing => (Strings.Get("NoteRinging"), HistoryTone.Accent),
        RingFate.Replied => (withTime && party.RepliedAt is { } at ? Strings.Format("NoteReply", party.Reply ?? "", TimeOf(at)) : party.Reply ?? "",
            HistoryTone.Success),
        RingFate.Closed => (Strings.Get("HistoryClosed"), HistoryTone.Neutral),
        RingFate.Quiet => (Strings.Get("HistoryQuiet"), HistoryTone.Caution),
        RingFate.Failed => (Strings.Get("HistoryFailed"), HistoryTone.Critical),
        _ => (Strings.Get("HistoryUnanswered"), HistoryTone.Neutral),
    };

    private static string TimeOf(DateTimeOffset at) => at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
}
