using System.Globalization;

namespace ExpressLuncher.Services;

/// <summary>Mise en forme des textes en français : durées, dates relatives, tailles, pluriels.</summary>
public static class Fr
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("fr-FR");

    public static string Plural(int count, string singular, string plural) => count < 2 ? singular : plural;

    public static string Games(int count) => count == 0 ? "Aucun jeu" : $"{count} {Plural(count, "jeu", "jeux")}";

    /// <summary>Durée courte : "42 min", "5 h 38", "126 h". Zéro donne "0 min".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        var totalMinutes = (long)Math.Round(duration.TotalMinutes);
        if (totalMinutes == 0 && duration.TotalSeconds >= 1) return "< 1 min";
        if (totalMinutes < 60) return $"{totalMinutes} min";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours >= 100 || minutes == 0) return $"{hours.ToString("N0", Culture)} h";
        return $"{hours} h {minutes:00}";
    }

    /// <summary>Date relative : "Aujourd'hui", "Hier", "Il y a 3 jours", "Il y a 4 sem.", "Il y a 5 mois", "Il y a 2 ans".</summary>
    public static string Relative(DateTime date) => Relative(date, DateTime.Now);

    public static string Relative(DateTime date, DateTime now)
    {
        var days = (now.Date - date.Date).Days;
        if (days <= 0) return "Aujourd'hui";
        if (days == 1) return "Hier";
        if (days < 14) return $"Il y a {days} jours";
        if (days < 60) return $"Il y a {days / 7} sem.";
        if (days < 365) return $"Il y a {days / 30} mois";
        var years = days / 365;
        return $"Il y a {years} {Plural(years, "an", "ans")}";
    }

    /// <summary>Même chose, pour le milieu d'une phrase ("hier", "il y a 4 sem.").</summary>
    public static string RelativeInSentence(DateTime date)
    {
        var text = Relative(date);
        return char.ToLower(text[0], Culture) + text[1..];
    }

    public static string DayAndTime(DateTime date)
    {
        var day = (DateTime.Now.Date - date.Date).Days switch
        {
            0 => "Aujourd'hui",
            1 => "Hier",
            _ => Capitalize(date.ToString(date.Year == DateTime.Now.Year ? "ddd d MMM" : "d MMM yyyy", Culture)),
        };
        return $"{day} · {date.ToString("HH:mm", Culture)}";
    }

    public static string ShortDay(DateOnly day) => day.ToString("d MMM", Culture);

    public static string Size(long bytes)
    {
        string[] units = ["octets", "Ko", "Mo", "Go", "To"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit < 2 || value >= 100 ? "0" : "0.#";
        return $"{value.ToString(format, Culture)} {units[unit]}";
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Culture) + text[1..];
}
