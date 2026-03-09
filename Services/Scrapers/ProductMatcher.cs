using System.Text.RegularExpressions;

namespace SmartShopper.API.Services.Scrapers;

/// <summary>
/// Gedeelde helper voor product-match scoring en generieke naam extractie.
/// Zorgt dat "AH rundergehakt 500g" en "Jumbo rundergehakt" en "Lidl rundergehakt"
/// allemaal als hetzelfde product worden herkend — zodat winkels eerlijk vergeleken worden.
/// </summary>
public static class ProductMatcher
{
    // ─── Winkelmerk-prefixen die weggestript worden ────────────────
    // "AH rundergehakt" → "rundergehakt"
    // "Jumbo halfvolle melk" → "halfvolle melk"
    private static readonly string[] StorePrefixes =
    [
        "albert heijn", "ah ", "jumbo", "lidl", "aldi", "plus",
        "dirk", "spar", "action", "kruidvat", "colruyt", "delhaize",
        "carrefour", "rewe", "edeka", "kaufland", "netto",
        "dagvers",
    ];

    // ─── Synoniemen voor veelgebruikte productnamen ────────────────
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["melk"]        = ["milk", "volle melk", "halfvolle melk", "lait"],
        ["brood"]       = ["bread", "pain", "brot", "boterham", "volkoren"],
        ["boter"]       = ["butter", "roomboter", "beurre"],
        ["kaas"]        = ["cheese", "fromage", "käse", "gouda", "edam"],
        ["eieren"]      = ["eier", "eggs", "ei", "oeufs"],
        ["appel"]       = ["apple", "pomme", "apfel", "appels"],
        ["tomaat"]      = ["tomato", "tomate", "tomaten"],
        ["kip"]         = ["chicken", "poulet", "hähnchen", "kipfilet"],
        ["varkens"]     = ["pork", "porc", "schwein"],
        ["rund"]        = ["beef", "boeuf", "rind", "runder", "rundergehakt"],
        ["gehakt"]      = ["hackfleisch", "haché", "mince", "rundergehakt", "half-om-half"],
        ["cola"]        = ["coca cola", "coca-cola", "coke"],
        ["water"]       = ["spa", "mineraalwater", "bronwater"],
        ["sinaasappel"] = ["orange", "sinas", "jus d'orange"],
        ["yoghurt"]     = ["yaourt", "joghurt", "yog"],
        ["rijst"]       = ["rice", "riz", "reis"],
        ["pasta"]       = ["spaghetti", "penne", "fusilli", "tagliatelle", "linguine"],
        ["chips"]       = ["crisps", "aardappelchips"],
        ["frisdrank"]   = ["soda", "fris", "limonade"],
    };

    // ─── Stopwoorden die geen matchwaarde hebben ───────────────────
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "de", "het", "een", "van", "met", "voor", "per", "in", "op", "aan",
        "the", "a", "an", "of", "with", "for", "per",
        "die", "das", "der", "ein", "eine", "mit", "für",
        "le", "la", "les", "du", "des", "avec", "pour",
        "pak", "zak", "fles", "blik", "doos", "stuks", "stuk"
    };

    /// <summary>
    /// Verwijdert winkelnamen en merkprefixen uit een productnaam.
    /// "AH rundergehakt 500g" → "rundergehakt 500g"
    /// "Jumbo halfvolle melk 1L" → "halfvolle melk 1L"
    /// </summary>
    public static string StripStoreBrand(string name)
    {
        var lower = name.ToLowerInvariant().Trim();
        foreach (var prefix in StorePrefixes)
        {
            if (lower.StartsWith(prefix.TrimEnd()))
            {
                // Verwijder het prefix + eventuele spatie erna
                var trimmed = name.Substring(prefix.TrimEnd().Length).TrimStart();
                if (trimmed.Length > 2) return trimmed;
            }
        }
        return name;
    }

    /// <summary>
    /// Geeft de generieke productnaam terug: strip winkelmerk + normaliseer.
    /// Gebruik dit als zoekterm bij andere scrapers.
    /// "AH rundergehakt 500g" → "rundergehakt"
    /// "Coca-Cola Zero 6-pack" → "Coca-Cola Zero" (A-merk bewaard)
    /// </summary>
    public static string GenericSearchName(string name)
    {
        // 1. Strip winkelmerk-prefix
        var stripped = StripStoreBrand(name);

        // 2. Normaliseer
        var normalized = Normalize(stripped);

        // 3. Haal keywords op (hoeveelheden/eenheden worden genegeerd bij zoeken)
        var keywords = GetKeywords(normalized);

        // Geef de keywords terug als zoekopdracht (max 4 woorden voor beste resultaten)
        return keywords.Length > 0
            ? string.Join(" ", keywords.Take(4))
            : stripped;
    }

    /// <summary>
    /// Berekent een nauwkeurige match-score tussen zoekopdracht en productnaam.
    /// Score 0.0–1.0, hoger = betere match.
    /// </summary>
    public static double Score(string query, string productName)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(productName))
            return 0;

        var q = Normalize(query);
        var p = Normalize(productName);

        // Exacte match
        if (q == p) return 1.0;

        // Product bevat volledige query
        if (p.Contains(q)) return 0.95;

        // Query bevat volledige productnaam
        if (q.Contains(p)) return 0.90;

        var qWords = GetKeywords(q);
        var pWords = GetKeywords(p);

        if (qWords.Length == 0) return 0;

        int hits = 0;
        foreach (var qw in qWords)
        {
            bool matched = pWords.Any(pw => pw == qw || pw.Contains(qw) || qw.Contains(pw));

            if (!matched && Synonyms.TryGetValue(qw, out var syns))
                matched = syns.Any(s => p.Contains(s));

            // Omgekeerde synonymen-check
            if (!matched)
                matched = Synonyms.Any(kv =>
                    kv.Value.Contains(qw, StringComparer.OrdinalIgnoreCase) &&
                    (p.Contains(kv.Key) || pWords.Any(pw => pw == kv.Key)));

            if (matched) hits++;
        }

        double baseScore = (double)hits / qWords.Length;

        if (hits == qWords.Length) baseScore = Math.Min(1.0, baseScore + 0.1);

        int extraPWords = pWords.Count(pw => !qWords.Any(qw => qw == pw || pw.Contains(qw)));
        if (extraPWords > 2) baseScore *= 0.85;

        return Math.Round(baseScore, 3);
    }

    public static bool IsReliableMatch(double score) => score >= 0.55;

    public static string Normalize(string input)
    {
        return input.ToLowerInvariant()
            .Replace("é", "e").Replace("è", "e").Replace("ê", "e")
            .Replace("ü", "u").Replace("ö", "o").Replace("ä", "a")
            .Replace("ï", "i").Replace("ç", "c")
            .Replace("-", " ").Replace("_", " ")
            .Replace("'", "").Replace("\"", "")
            .Trim();
    }

    private static string[] GetKeywords(string normalized)
    {
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !Stopwords.Contains(w) && !IsUnit(w) && !IsNumber(w))
            .ToArray();
    }

    private static bool IsUnit(string w) =>
        Regex.IsMatch(w, @"^\d*(g|kg|ml|l|cl|liter|gram|stuks?|pak|fles|blik|doos)s?$");

    private static bool IsNumber(string w) => Regex.IsMatch(w, @"^\d+[\.,]?\d*$");
}
