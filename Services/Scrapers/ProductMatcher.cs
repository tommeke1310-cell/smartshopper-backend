using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace SmartShopper.API.Services.Scrapers;

// ================================================================
//  ProductMatcher — gedeelde matching-logica voor alle scrapers
//
//  Verbeteringen t.o.v. oude WordScore:
//   1. Accent-normalisatie  : 'crème' == 'creme', 'ü' == 'u'
//   2. Synoniem-mapping     : 'friet' == 'patat', 'mayo' == 'mayonaise'
//   3. Stemming             : 'croissants' == 'croissant', 'ijsje' == 'ijs'
//   4. Samengestelde woorden: 'sojasaus' matcht op 'soja saus'
//   5. Merkproduct detectie : merken hebben vaste prijs, geen multiplier nodig
//   6. Hogere minimumdrempel: score < 0.5 geen match (was: geen drempel)
// ================================================================
public static class ProductMatcher
{
    // ── 1. Synoniem mapping (NL <-> NL dialecten/afkortingen/EN) ──────────
    private static readonly Dictionary<string, string[]> Synoniemen = new(StringComparer.OrdinalIgnoreCase)
    {
        // Vlees & Vis
        ["kipfilet"]         = new[] { "chicken filet", "chicken breast", "kip filet", "kippenfilet" },
        ["kip"]              = new[] { "chicken", "poulet" },
        ["rundergehakt"]     = new[] { "runder gehakt", "gehakt rund", "beef mince", "gehakt rund half" },
        ["gehakt"]           = new[] { "mince", "farce", "rundergehakt", "half om half" },
        ["varkenshaas"]      = new[] { "varkens haas", "pork tenderloin" },
        ["bacon"]            = new[] { "ontbijtspek", "spek", "speklapjes" },
        ["speklapjes"]       = new[] { "bacon", "ontbijtspek", "spek" },
        ["biefstuk"]         = new[] { "beef steak", "steak" },
        ["garnalen"]         = new[] { "shrimps", "crevetten", "gambas" },
        ["zalmfilet"]        = new[] { "zalm filet", "salmon fillet", "salmon" },
        ["tonijn"]           = new[] { "tuna", "thon" },
        ["kabeljauw"]        = new[] { "cod", "cabillaud" },
        // Zuivel
        ["halfvolle melk"]   = new[] { "half vol melk", "halfvolle", "demi-ecreme" },
        ["volle melk"]       = new[] { "vol melk", "volle", "entier", "volle liter" },
        ["magere melk"]      = new[] { "mager melk", "skimmed milk" },
        ["boter"]            = new[] { "roomboter", "butter", "beurre" },
        ["roomboter"]        = new[] { "boter", "butter" },
        ["creme fraiche"]    = new[] { "creme fraiche", "zure room", "sour cream" },
        ["kwark"]            = new[] { "fromage blanc", "quark" },
        ["yoghurt"]          = new[] { "yogurt", "yaourt" },
        ["kaas"]             = new[] { "cheese", "fromage", "kase" },
        ["smeerkaas"]        = new[] { "cream cheese", "philadelphia" },
        // Drogisterij
        ["wc papier"]        = new[] { "toiletpapier", "toilet paper", "wc-papier", "wcpapier" },
        ["toiletpapier"]     = new[] { "wc papier", "wc-papier", "toilet paper" },
        ["tandpasta"]        = new[] { "toothpaste", "dentifrice", "tandgel" },
        ["scheerschuim"]     = new[] { "scheerzeep", "shaving foam" },
        ["douchegel"]        = new[] { "douche gel", "shower gel", "badgel" },
        ["wasmiddel"]        = new[] { "waspoeder", "wasgel", "lessive", "laundry" },
        ["afwasmiddel"]      = new[] { "afwas middel", "washing-up liquid", "liquide vaisselle" },
        ["deodorant"]        = new[] { "deo", "deospray", "antiperspirant" },
        // Dranken
        ["cola"]             = new[] { "coca-cola", "coca cola", "pepsi", "cola zero", "cola light" },
        ["sinaasappelsap"]   = new[] { "sinaasappel sap", "jus d orange", "orange juice", "appelsientje sinaasappel" },
        ["appelsap"]         = new[] { "appel sap", "apple juice" },
        ["bier"]             = new[] { "pils", "lager", "biere" },
        ["wijn"]             = new[] { "wine", "vin", "wein" },
        ["koffie"]           = new[] { "coffee", "cafe", "kafee" },
        ["thee"]             = new[] { "tea", "the", "tee" },
        ["water"]            = new[] { "bronwater", "bruiswater", "mineral water" },
        // Groente & Fruit
        ["aardappelen"]      = new[] { "aardappels", "potatoes", "pommes de terre" },
        ["aardappel"]        = new[] { "potato", "aardappels" },
        ["tomaten"]          = new[] { "tomaat", "tomatoes", "tomates" },
        ["ui"]               = new[] { "uien", "onion", "oignon" },
        ["knoflook"]         = new[] { "garlic", "ail" },
        ["spinazie"]         = new[] { "spinach", "epinards" },
        ["sla"]              = new[] { "ijsbergsla", "lettuce", "salade" },
        ["champignons"]      = new[] { "paddenstoelen", "mushrooms", "champignon" },
        ["wortel"]           = new[] { "wortelen", "peen", "carrot" },
        ["appel"]            = new[] { "apple", "appels" },
        ["banaan"]           = new[] { "banana", "bananen" },
        ["aardbei"]          = new[] { "strawberry", "aardbeien" },
        // Brood & Bakkerij
        ["brood"]            = new[] { "boterham", "bread", "pain", "brot" },
        ["wit brood"]        = new[] { "wit tarwebrood", "witbrood", "white bread" },
        ["volkoren brood"]   = new[] { "volkorenbrood", "wholegrain bread", "pain complet" },
        ["croissant"]        = new[] { "croissants", "croissantje" },
        ["crackers"]         = new[] { "cracker", "knackebrod" },
        // Diepvries
        ["friet"]            = new[] { "patat", "frites", "french fries", "frieten", "patates frites" },
        ["patat"]            = new[] { "friet", "frites", "french fries" },
        ["ijsje"]            = new[] { "ijs", "ice cream", "glace", "roomijs" },
        ["ijs"]              = new[] { "ijsje", "ice cream", "glace", "roomijs" },
        ["erwten"]           = new[] { "doperwten", "peas", "petits pois" },
        ["doperwten"]        = new[] { "erwten", "peas" },
        // Pasta & Graan
        ["pasta"]            = new[] { "spaghetti", "penne", "fusilli", "macaroni", "tagliatelle" },
        ["spaghetti"]        = new[] { "pasta" },
        ["rijst"]            = new[] { "rice", "riz", "reis" },
        // Sauzen
        ["mayo"]             = new[] { "mayonaise", "mayonnaise" },
        ["mayonaise"]        = new[] { "mayo", "mayonnaise" },
        ["ketchup"]          = new[] { "tomatenketchup", "tomato ketchup" },
        ["sojasaus"]         = new[] { "soja saus", "soy sauce", "sojasosse" },
        ["olijfolie"]        = new[] { "olijf olie", "olive oil", "huile d olive" },
        // Divers
        ["eieren"]           = new[] { "ei", "kippenei", "eggs", "oeufs", "eier" },
        ["chocolade"]        = new[] { "chocola", "chocolate", "schokolade" },
        ["chips"]            = new[] { "crisps", "snacks" },
        ["noten"]            = new[] { "nuts", "mixed nuts", "noix", "cashewnoten", "amandelen", "walnoten" },
        ["mosterd"]          = new[] { "mustard", "moutarde", "senf", "zaanse mosterd" },
        ["zure room"]        = new[] { "creme fraiche", "sour cream", "zuivelproduct" },
        ["ontbijtgranen"]    = new[] { "cornflakes", "muesli", "granola", "granenontbijt", "kelloggs" },
        ["ham"]              = new[] { "gekookte ham", "ham naturel", "achterham", "voorham", "jambon" },
    };

    // ── 2. Merkproducten met vaste prijs (2025-prijzen, AH als referentie) ──
    public static readonly Dictionary<string, decimal> MerkPrijzen = new(StringComparer.OrdinalIgnoreCase)
    {
        // Frisdrank 1.5L
        ["coca-cola"]           = 1.99m,
        ["pepsi"]               = 1.89m,
        ["fanta"]               = 1.89m,
        ["sprite"]              = 1.89m,
        ["7up"]                 = 1.89m,
        // Bier per fles
        ["heineken"]            = 1.05m,
        ["amstel"]              = 0.99m,
        ["grolsch"]             = 1.09m,
        ["hertog jan"]          = 1.09m,
        ["jupiler"]             = 0.99m,
        ["stella artois"]       = 1.05m,
        // Energy
        ["red bull"]            = 1.99m,
        ["monster energy"]      = 1.89m,
        // Koffie
        ["douwe egberts"]       = 6.49m,
        ["nescafe"]             = 5.99m,
        // Zuivel merk
        ["activia"]             = 2.49m,
        ["alpro"]               = 2.29m,
        // Chocolade & Snoep
        ["nutella"]             = 3.99m,
        ["lotus"]               = 2.49m,
        ["oreo"]                = 2.49m,
        ["kitkat"]              = 1.49m,
        ["haribo"]              = 1.49m,
        // Chips
        ["lays"]                = 2.49m,
        ["lay's"]               = 2.49m,
        ["pringles"]            = 2.99m,
        ["doritos"]             = 2.49m,
        // Sauzen
        ["calve"]               = 2.99m,
        ["heinz"]               = 2.49m,
        ["hellmanns"]           = 2.99m,
        ["kikkoman"]            = 3.49m,
        // Huishouden
        ["ariel"]               = 9.99m,
        ["persil"]              = 8.99m,
        ["robijn"]              = 8.49m,
        ["dreft"]               = 3.99m,
        ["fairy"]               = 3.49m,
        ["domestos"]            = 2.99m,
        ["dettol"]              = 3.99m,
        // Verzorging
        ["dove"]                = 3.49m,
        ["axe"]                 = 3.99m,
        ["nivea"]               = 3.99m,
        ["colgate"]             = 2.99m,
        ["sensodyne"]           = 5.99m,
    };

    // ── 3. Normaliseer string: accenten weg + lowercase + trim ──────────
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return Regex.Replace(sb.ToString().ToLowerInvariant().Trim(), @"\s+", " ");
    }

    // ── 4. Stemming: verwijder meervoud/verkleinwoord-suffixen ──────────
    public static string Stem(string word)
    {
        if (word.Length <= 3) return word;
        if (word.EndsWith("jes"))  return word[..^3];
        if (word.EndsWith("tje"))  return word[..^3];
        if (word.EndsWith("je"))   return word[..^2];
        if (word.EndsWith("ssen")) return word[..^2];
        if (word.EndsWith("nen"))  return word[..^2];
        if (word.EndsWith("ren"))  return word[..^2];
        if (word.EndsWith("ten"))  return word[..^2];
        if (word.EndsWith("ken"))  return word[..^2];
        if (word.EndsWith("gen"))  return word[..^2];
        if (word.EndsWith("len"))  return word[..^2];
        if (word.EndsWith("ven"))  return word[..^2];
        if (word.EndsWith("en") && word.Length > 5) return word[..^2];
        if (word.EndsWith("s")  && word.Length > 4) return word[..^1];
        return word;
    }

    // ── 5. Haal synoniem-uitbreidingen op ───────────────────────────────
    private static IEnumerable<string> GetSynoniemen(string word)
    {
        yield return word;
        if (Synoniemen.TryGetValue(word, out var syns))
            foreach (var s in syns)
                yield return Normalize(s);
        // Omgekeerde mapping
        foreach (var kvp in Synoniemen)
        {
            var normKey = Normalize(kvp.Key);
            if (normKey == word)
                continue;
            if (kvp.Value.Select(Normalize).Contains(word))
            {
                yield return normKey;
                foreach (var s in kvp.Value)
                    yield return Normalize(s);
            }
        }
    }

    // ── 6. Hoofd MatchScore (vervangt WordScore in alle scrapers) ────────
    public static double MatchScore(string query, string productName)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(productName))
            return 0;

        var qNorm = Normalize(query);
        var pNorm = Normalize(productName);

        // Exacte match
        if (qNorm == pNorm) return 1.0;

        // Samengesteld: 'sojasaus' matcht op 'soja saus'
        var qComp = qNorm.Replace(" ", "");
        var pComp = pNorm.Replace(" ", "");
        if (pComp.Contains(qComp) || qComp.Contains(pComp))
            return 0.9;

        // Multi-word query directe synoniem lookup (bijv. 'zure room' -> 'creme fraiche')
        if (Synoniemen.TryGetValue(qNorm, out var directSyns))
        {
            foreach (var syn in directSyns)
            {
                var synNorm = Normalize(syn);
                if (pNorm.Contains(synNorm) || synNorm.Contains(pNorm))
                    return 0.85;
            }
        }

        var queryWords   = qNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var productWords = pNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var productStemmedSet = productWords.Select(Stem).ToHashSet(StringComparer.Ordinal);

        double matchedWeight = 0;
        double totalWeight   = 0;

        foreach (var qWord in queryWords)
        {
            // Kortere woorden wegen minder
            double weight = qWord.Length >= 5 ? 1.0 : qWord.Length >= 3 ? 0.6 : 0.2;
            totalWeight += weight;

            var qStemmed    = Stem(qWord);
            var allVariants = GetSynoniemen(qWord)
                .Concat(GetSynoniemen(qStemmed))
                .Select(Normalize)
                .Distinct()
                .ToList();

            bool found = allVariants.Any(variant =>
                pNorm.Contains(variant) ||
                productStemmedSet.Contains(Stem(variant)) ||
                productWords.Any(pw =>
                    pw.StartsWith(variant, StringComparison.Ordinal) ||
                    variant.StartsWith(pw, StringComparison.Ordinal)));

            if (found) matchedWeight += weight;
        }

        if (totalWeight == 0) return 0;
        double score = matchedWeight / totalWeight;

        // Kleine bonus als product-woorden ook in query voorkomen
        int reverseHits = productWords
            .Where(pw => pw.Length >= 4)
            .Count(pw => qNorm.Contains(pw) ||
                         queryWords.Any(qw => Stem(qw) == Stem(pw)));
        double bonus = Math.Min(0.1, reverseHits * 0.03);

        return Math.Min(1.0, score + bonus);
    }

    // ── 7. Verrijk zoekterm voor betere API-resultaten ──────────────────
    public static string NormalizeQueryForSearch(string query)
    {
        var q = Normalize(query);
        // Vervang bekende afkortingen door volledige zoektermen
        q = q.Replace("mayo", "mayonaise")
             .Replace("wc papier", "toiletpapier")
             .Replace("wcpapier",  "toiletpapier")
             .Replace("friet",     "patat")
             .Replace("ijsje",     "ijs");
        return q.Trim();
    }

    // ── 8. Merkprijs opzoeken ────────────────────────────────────────────
    public static decimal? GetMerkPrijs(string query)
    {
        var q = Normalize(query);
        foreach (var kvp in MerkPrijzen)
        {
            if (q.Contains(Normalize(kvp.Key)))
                return kvp.Value;
        }
        return null;
    }

    public static bool IsMerkProduct(string query) => GetMerkPrijs(query) != null;

    // ── 9. Alias voor backwards-compat (gebruikt in CompareService & BackgroundScraperService) ──
    public static double Score(string query, string productName) => MatchScore(query, productName);

    // ── 10. Strip winkelketen-prefix zodat AH-productnamen ook bij Jumbo/Lidl/etc. zoeken ──
    //  "AH rundergehakt 500g" → "rundergehakt"
    //  "Coca-Cola Zero"       → "Coca-Cola Zero"  (A-merk blijft intact)
    private static readonly HashSet<string> StorePrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        "ah", "albert heijn", "jumbo", "lidl", "aldi", "plus", "dirk",
        "colruyt", "delhaize", "carrefour", "rewe", "edeka", "kaufland", "penny",
    };

    public static string GenericSearchName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var parts = name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && StorePrefix.Contains(parts[0]))
            return parts[1].Trim();
        return name.Trim();
    }
}
