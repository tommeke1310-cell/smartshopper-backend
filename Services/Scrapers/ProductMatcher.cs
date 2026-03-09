using System.Text.RegularExpressions;

namespace SmartShopper.API.Services.Scrapers;

/// <summary>
/// Gedeelde helper voor product-match scoring en hoeveelheid-normalisatie.
/// Zorgt dat alle scrapers op dezelfde manier vergelijken → nauwkeurigere prijsvergelijking.
/// </summary>
public static class ProductMatcher
{
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
        ["rund"]        = ["beef", "boeuf", "rind"],
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
    /// Berekent een nauwkeurige match-score tussen zoekopdracht en productnaam.
    /// Houdt rekening met synoniemen, hoeveelheden en stopwoorden.
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

        // Query bevat volledige productnaam (product is subsetvan query)
        if (q.Contains(p)) return 0.90;

        var qWords = GetKeywords(q);
        var pWords = GetKeywords(p);

        if (qWords.Length == 0) return 0;

        // Tel directe treffers
        int hits = 0;
        var unmatchedQ = new List<string>();

        foreach (var qw in qWords)
        {
            bool matched = pWords.Any(pw => pw == qw || pw.Contains(qw) || qw.Contains(pw));

            // Check synoniemen als geen directe hit
            if (!matched && Synonyms.TryGetValue(qw, out var syns))
                matched = syns.Any(s => p.Contains(s));

            if (matched) hits++;
            else unmatchedQ.Add(qw);
        }

        double baseScore = (double)hits / qWords.Length;

        // Bonus: als alle relevante querywoorden matchen
        if (hits == qWords.Length) baseScore = Math.Min(1.0, baseScore + 0.1);

        // Kleine straf voor ongematchte woorden in product
        // (product is te specifiek of ander merk/type)
        int extraPWords = pWords.Count(pw => !qWords.Any(qw => qw == pw || pw.Contains(qw)));
        if (extraPWords > 2) baseScore *= 0.85;

        return Math.Round(baseScore, 3);
    }

    /// <summary>
    /// Geeft true als de score hoog genoeg is voor een betrouwbare vergelijking.
    /// </summary>
    public static bool IsReliableMatch(double score) => score >= 0.60;

    /// <summary>
    /// Normaliseert productnaam voor vergelijking (lowercase, diacritics weg, etc.)
    /// </summary>
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
