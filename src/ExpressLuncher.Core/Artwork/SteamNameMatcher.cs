using System.Globalization;
using System.Text;

namespace ExpressLuncher.Core.Artwork;

/// <summary>Un résultat de la recherche du magasin Steam.</summary>
internal readonly record struct StoreCandidate(long AppId, string Name);

/// <summary>
/// Rapproche le nom d'un jeu de la bibliothèque des résultats du magasin Steam. Volontairement strict :
/// une mauvaise jaquette est pire que pas de jaquette.
/// </summary>
internal static class SteamNameMatcher
{
    /// <summary>En dessous, un nom est trop court pour être rapproché sans risque.</summary>
    public const int MinimumKeyLength = 3;

    // Mentions d'édition ignorées en fin de nom, écrites comme les mots découpés (sans accent ni apostrophe).
    private static readonly string[][] EditionSuffixes = new[]
        {
            "game of the year edition", "game of the year", "goty edition", "goty",
            "digital deluxe edition", "deluxe edition", "definitive edition", "ultimate edition",
            "complete edition", "enhanced edition", "gold edition", "standard edition", "special edition",
            "anniversary edition", "premium edition", "legendary edition", "collectors edition",
            "directors cut", "remastered", "enhanced",
        }
        .Select(phrase => phrase.Split(' '))
        .OrderByDescending(words => words.Length)
        .ToArray();

    /// <summary>
    /// Clé de comparaison : même règle que ScanHelpers.Normalize (lettres et chiffres en minuscules),
    /// sans accents ni symboles de marque, « &amp; » lu « and ».
    /// </summary>
    public static string Key(string? name) => string.Concat(Tokenize(name).Select(word => word.Text));

    /// <summary>Terme à envoyer à la recherche : le nom sans symboles de marque.</summary>
    public static string SearchTerm(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (!IsTrademark(c)) sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
        return CollapseSpaces(sb.ToString());
    }

    /// <summary>Le terme de recherche sans mention d'édition, ou null s'il n'y en avait pas.</summary>
    public static string? CoreSearchTerm(string name)
    {
        var term = SearchTerm(name);
        var words = Tokenize(term);
        var coreLength = CoreLength(words);
        if (coreLength == words.Count) return null;

        var core = term[..words[coreLength].Start].TrimEnd(' ', '-', '–', '—', ':', ',', '(', '[');
        return core.Length == 0 ? null : core;
    }

    /// <summary>
    /// Choisit le résultat qui désigne le même jeu : nom identique d'abord, sinon identique une fois
    /// les mentions d'édition retirées des deux côtés. Null si aucun n'est sûr.
    /// </summary>
    public static StoreCandidate? Pick(string name, IReadOnlyList<StoreCandidate> candidates)
    {
        var wanted = Tokenize(name);
        var wantedKey = Join(wanted, wanted.Count);
        if (wantedKey.Length < MinimumKeyLength) return null;
        var wantedCore = Join(wanted, CoreLength(wanted));

        StoreCandidate? sameCore = null;
        foreach (var candidate in candidates)
        {
            var words = Tokenize(candidate.Name);
            if (Join(words, words.Count) == wantedKey) return candidate;
            if (sameCore is null && Join(words, CoreLength(words)) == wantedCore) sameCore = candidate;
        }
        return sameCore;
    }

    private readonly record struct Word(string Text, int Start);

    private static List<Word> Tokenize(string? text)
    {
        var words = new List<Word>();
        if (string.IsNullOrEmpty(text)) return words;

        var sb = new StringBuilder();
        var start = 0;

        void Flush()
        {
            if (sb.Length == 0) return;
            words.Add(new Word(sb.ToString(), start));
            sb.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Apostrophes et symboles de marque ne coupent pas le mot : "Assassin's" → "assassins".
            if (c is '\'' or '’' or '‘' or '`' || IsTrademark(c)) continue;

            if (char.IsLetterOrDigit(c))
            {
                if (sb.Length == 0) start = i;
                sb.Append(Fold(c));
                continue;
            }

            Flush();
            if (c == '&') words.Add(new Word("and", i));
        }
        Flush();
        return words;
    }

    /// <summary>Nombre de mots restants une fois les mentions d'édition finales retirées (au moins un).</summary>
    private static int CoreLength(List<Word> words)
    {
        var length = words.Count;
        while (true)
        {
            var suffix = EditionSuffixes.FirstOrDefault(s => length > s.Length && EndsWith(words, length, s));
            if (suffix is null) return length;

            length -= suffix.Length;
            // "… – The Definitive Edition"
            if (length > 1 && words[length - 1].Text == "the") length--;
        }
    }

    private static bool EndsWith(List<Word> words, int length, string[] suffix)
    {
        for (var i = 0; i < suffix.Length; i++)
            if (words[length - suffix.Length + i].Text != suffix[i]) return false;
        return true;
    }

    private static string Join(List<Word> words, int count) => string.Concat(words.Take(count).Select(word => word.Text));

    private static bool IsTrademark(char c) => c is '™' or '®' or '©' or '℠';

    private static char Fold(char c)
    {
        if (c < 128) return char.ToLowerInvariant(c);

        foreach (var part in c.ToString().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark)
                return char.ToLowerInvariant(part);
        return char.ToLowerInvariant(c);
    }

    private static string CollapseSpaces(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
