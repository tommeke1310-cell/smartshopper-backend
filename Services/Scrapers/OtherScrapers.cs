// OtherScrapers.cs
// Let op: JumboScraper is VERWIJDERD uit dit bestand.
// De enige JumboScraper staat in JumboScraper.cs (mobile API v17).
//
// LidlScraper en AldiScraper zijn wrapper-adapters die de implementaties
// uit GermanDutchScrapers.cs aanroepen en de resultaten omzetten naar
// List<ProductMatch> (uniform interface voor CompareService).

using SmartShopper.API.Models;

namespace SmartShopper.API.Services.Scrapers;

// Niets meer te zien hier — LidlScraper en AldiScraper leven volledig
// in GermanDutchScrapers.cs en zijn al geregistreerd in Program.cs.
// Dit bestand is bewaard om geen build-fouten te veroorzaken bij
// eventuele oude using-statements.
