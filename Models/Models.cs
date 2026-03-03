using System;
using System.Collections.Generic;

namespace SmartShopper.API.Models
{
    // --- REQUEST MODELS (Wat komt er binnen vanuit Expo?) ---

    public class CompareRequest
    {
        public List<ProductItem> Items { get; set; } = new();
        public double UserLatitude { get; set; }
        public double UserLongitude { get; set; }
        public int MaxDistanceKm { get; set; } = 25;
        public bool IncludeBelgium { get; set; } = true;
        public bool IncludeGermany { get; set; } = true;
        public decimal FuelConsumptionLPer100Km { get; set; } = 7.0m;
    }

    public class ProductItem
    {
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
    }

    // --- SCRAPER MODELS (Wat geven de scrapers terug?) ---

    public class ScraperResult
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public bool Success { get; set; }

        public ScraperResult() { }

        public ScraperResult(string name, decimal price, bool success)
        {
            Name = name;
            Price = price;
            Success = success;
        }
    }

    // --- RESPONSE MODELS (Wat sturen we terug naar de App?) ---

    public class CompareResult
    {
        public List<StoreComparison> Stores { get; set; } = new();
        public StoreComparison? BestDeal { get; set; }
        public decimal MaxSavings { get; set; }
        public FuelPrices? FuelPrices { get; set; }
    }

    public class StoreComparison
    {
        public StoreTemplate Store { get; set; } = new();
        public List<ScraperResult> Products { get; set; } = new();
        public decimal GroceryTotal { get; set; }
        public decimal FuelCostEur { get; set; }
        public decimal TotalCost { get; set; }
        public decimal SavingsVsReference { get; set; }
        public bool IsBestDeal { get; set; }
    }

    public class StoreTemplate
    {
        public string Chain { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty; // NL, BE, DE
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double DistanceKm { get; set; }
        public int DriveTimeMinutes { get; set; }
        public string? CrossBorderTip { get; set; }
    }

    public class FuelPrices
    {
        public decimal NL { get; set; }
        public decimal BE { get; set; }
        public decimal DE { get; set; }
    }
}