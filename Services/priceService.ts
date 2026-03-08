// ============================================================
// priceService.ts — Crowdsourced prijsdatabase service
// Koppelt bonnetjes, vergelijkingen en API data aan Supabase
// Importeer in App.tsx: import { priceService } from './priceService';
// ============================================================

import { createClient } from '@supabase/supabase-js';
import AsyncStorage from '@react-native-async-storage/async-storage';
import Constants from 'expo-constants';

// ── Configuratie via environment variables ────────────────────
// Stel in via .env (lokaal) of EAS Secrets (productie):
//   EXPO_PUBLIC_SUPABASE_URL=https://xxxx.supabase.co
//   EXPO_PUBLIC_SUPABASE_ANON_KEY=eyJ...
// Nooit hardcoden in broncode — risico op credential-lek via git.
const SUPABASE_URL = (
  Constants.expoConfig?.extra?.supabaseUrl as string | undefined
) ?? process.env.EXPO_PUBLIC_SUPABASE_URL ?? '';

const SUPABASE_ANON_KEY = (
  Constants.expoConfig?.extra?.supabaseAnonKey as string | undefined
) ?? process.env.EXPO_PUBLIC_SUPABASE_ANON_KEY ?? '';

if (!SUPABASE_URL || !SUPABASE_ANON_KEY) {
  console.error(
    '[PriceService] ⚠️  SUPABASE_URL of SUPABASE_ANON_KEY ontbreekt. ' +
    'Voeg EXPO_PUBLIC_SUPABASE_URL en EXPO_PUBLIC_SUPABASE_ANON_KEY toe aan .env of EAS Secrets.',
  );
}

const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY, {
  auth: { storage: AsyncStorage, autoRefreshToken: true, persistSession: true },
});

// ── Types ─────────────────────────────────────────────────────

export type PriceSource = 'receipt' | 'api_ah' | 'api_jumbo' | 'manual' | 'scrape';

export interface PriceReport {
  productName:  string;
  storeChain:   string;
  storeCity?:   string;
  price:        number;
  source:       PriceSource;
  confidence:   number;        // 0.0 - 1.0
  receiptDate?: string;        // ISO date string
  onSale?:      boolean;
  saleLabel?:   string;
  reportLat?:   number;
  reportLng?:   number;
}

export interface PriceLookupResult {
  price:        number;
  source:       string;       // 'supabase' | 'api_ah' | 'api_jumbo' | 'estimate'
  confidence:   number;
  reportCount:  number;
  lastReported: string | null;
  available:    boolean;
}

export interface ReceiptUploadPayload {
  storeChain?:  string;
  storeCity?:   string;
  receiptDate?: string;
  totalAmount?: number;
  ocrText:      string;
  parsedItems:  Array<{ name: string; price: number; quantity: number }>;
  lat?:         number;
  lng?:         number;
}

// ── In-memory cache ───────────────────────────────────────────
// Voorkomt dubbele Supabase queries binnen dezelfde sessie
const lookupCache = new Map<string, { result: PriceLookupResult; cachedAt: number }>();
const CACHE_TTL   = 30 * 60 * 1000; // 30 minuten

// Offline queue: prijsrapports die gestuurd worden zodra er verbinding is
const offlineQueue: PriceReport[] = [];

// ── Hulpfunctie: productnaam normaliseren ─────────────────────
// "AH Halfvolle Melk 1L" → "halfvolle melk 1l"
// Zodat "Halfvolle melk", "halfvolle melk AH", "HALFVOLLE MELK" allemaal matchen
function normalizeProductName(name: string): string {
  return name
    .toLowerCase()
    .replace(/\b(ah|albert heijn|jumbo|lidl|aldi|plus|dirk|spar|huismerk|private label)\b/g, '')
    .replace(/[^a-z0-9\s\.]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

// ── 1. Prijs opzoeken in Supabase ─────────────────────────────
// Geeft de mediaan prijs terug op basis van crowdsourced data
// van de afgelopen 30 dagen voor een product × winkelketen combinatie
export async function lookupPrice(
  productName: string,
  storeChain:  string,
  storeCity?:  string,
): Promise<PriceLookupResult | null> {
  const cacheKey = `${normalizeProductName(productName)}|${storeChain}|${storeCity ?? ''}`;
  const cached   = lookupCache.get(cacheKey);
  if (cached && Date.now() - cached.cachedAt < CACHE_TTL) return cached.result;

  try {
    // Eerst proberen via de materialized view (snelst)
    const { data: agg } = await supabase
      .from('price_aggregates')
      .select('median_price, report_count, confidence_score, last_reported')
      .ilike('product_name', `%${normalizeProductName(productName)}%`)
      .eq('store_chain', storeChain)
      .order('confidence_score', { ascending: false })
      .limit(1)
      .single();

    if (agg && agg.median_price > 0) {
      const result: PriceLookupResult = {
        price:        Number(agg.median_price),
        source:       'supabase',
        confidence:   Number(agg.confidence_score),
        reportCount:  agg.report_count,
        lastReported: agg.last_reported,
        available:    true,
      };
      lookupCache.set(cacheKey, { result, cachedAt: Date.now() });
      return result;
    }

    // Fallback: directe query op prices tabel (als view nog leeg is)
    const { data: recent } = await supabase
      .from('prices')
      .select('price, confidence, reported_at')
      .ilike('product_name', `%${normalizeProductName(productName)}%`)
      .eq('store_chain', storeChain)
      .eq('flagged', false)
      .gte('reported_at', new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString())
      .order('reported_at', { ascending: false })
      .limit(10);

    if (recent && recent.length > 0) {
      const prices = recent.map(r => Number(r.price)).sort((a, b) => a - b);
      const median = prices[Math.floor(prices.length / 2)];
      const result: PriceLookupResult = {
        price:        median,
        source:       'supabase',
        confidence:   Math.min(1.0, recent.length * 0.1),
        reportCount:  recent.length,
        lastReported: recent[0].reported_at,
        available:    true,
      };
      lookupCache.set(cacheKey, { result, cachedAt: Date.now() });
      return result;
    }

    return null; // Geen data in Supabase — caller gebruikt AH/Jumbo API of schatting
  } catch (e) {
    console.warn('[PriceService] Supabase lookup mislukt:', e);
    return null;
  }
}

// ── 2. Prijs rapporteren naar Supabase ────────────────────────
// Wordt aangeroepen vanuit recordPrice() in App.tsx
export async function reportPrice(report: PriceReport): Promise<void> {
  const { data: { user } } = await supabase.auth.getUser();

  const payload = {
    product_name: report.productName,
    store_chain:  report.storeChain,
    store:        report.storeChain,   // bestaande kolom ook vullen
    store_city:   report.storeCity ?? null,
    price:        report.price,
    source:       report.source,
    confidence:   report.confidence,
    reported_by:  user?.id ?? null,
    receipt_date: report.receiptDate ?? null,
    is_promo:     report.onSale ?? false,
    sale_label:   report.saleLabel ?? null,
    report_lat:   report.reportLat ?? null,
    report_lng:   report.reportLng ?? null,
    reported_at:  new Date().toISOString(),
  };

  try {
    const { error } = await supabase.from('prices').insert(payload);
    if (error) {
      console.warn('[PriceService] Insert mislukt, in queue:', error.message);
      offlineQueue.push(report);
    } else {
      // Cache invalideren zodat de volgende lookup verse data krijgt
      const cacheKey = `${normalizeProductName(report.productName)}|${report.storeChain}|${report.storeCity ?? ''}`;
      lookupCache.delete(cacheKey);
    }
  } catch {
    // Geen verbinding — zet in offline queue
    offlineQueue.push(report);
  }
}

// ── 3. Bonnetje uploaden ──────────────────────────────────────
// Slaat het volledige bonnetje op én rapporteert alle producten als prijzen
export async function uploadReceipt(
  payload: ReceiptUploadPayload,
  userId?: string,
): Promise<{ pointsEarned: number; badgeLevel: string | null }> {
  const { data: { user } } = await supabase.auth.getUser();
  const resolvedUserId = userId ?? user?.id ?? null;

  // 1. Bonnetje opslaan
  const { error: receiptError } = await supabase.from('receipt_uploads').insert({
    user_id:      resolvedUserId,
    store_chain:  payload.storeChain ?? null,
    store_city:   payload.storeCity ?? null,
    receipt_date: payload.receiptDate ?? null,
    total_amount: payload.totalAmount ?? null,
    item_count:   payload.parsedItems.length,
    ocr_text:     payload.ocrText,
    parsed_items: payload.parsedItems,
    lat:          payload.lat ?? null,
    lng:          payload.lng ?? null,
  });

  if (receiptError) {
    console.warn('[PriceService] Receipt upload mislukt:', receiptError.message);
    return { pointsEarned: 0, badgeLevel: null };
  }

  // 2. Elk product als aparte prijsmelding opslaan
  const priceInserts = payload.parsedItems
    .filter(item => item.price > 0)
    .map(item => ({
      product_name: item.name,
      store_chain:  payload.storeChain ?? 'Onbekend',
      store_city:   payload.storeCity ?? null,
      price:        item.price,
      source:       'receipt' as PriceSource,
      store:        payload.storeChain ?? 'Onbekend',
      confidence:   0.85,        // Bonnetje = hoge confidence
      reported_by:  resolvedUserId,
      receipt_date: payload.receiptDate ?? null,
      report_lat:   payload.lat ?? null,
      report_lng:   payload.lng ?? null,
    }));

  if (priceInserts.length > 0) {
    await supabase.from('prices').insert(priceInserts);
  }

  // 3. Gamification punten ophalen
  if (resolvedUserId) {
    const { data: stats } = await supabase
      .from('user_price_stats')
      .select('points_total, badge_level')
      .eq('user_id', resolvedUserId)
      .single();
    const pointsEarned = 10 + payload.parsedItems.length * 2;
    return { pointsEarned, badgeLevel: stats?.badge_level ?? 'Starter' };
  }

  return { pointsEarned: 0, badgeLevel: null };
}

// ── 4. Offline queue verwerken ────────────────────────────────
// Aanroepen bij app-start of als netwerk herstelt
export async function flushOfflineQueue(): Promise<void> {
  if (offlineQueue.length === 0) return;
  const toSend = [...offlineQueue];
  offlineQueue.length = 0;

  for (const report of toSend) {
    await reportPrice(report).catch(() => offlineQueue.push(report));
  }
  if (offlineQueue.length > 0) {
    console.warn(`[PriceService] ${offlineQueue.length} items nog in queue na flush`);
  }
}

// ── 5. Gebruikersstats ophalen ────────────────────────────────
export async function getUserPriceStats(): Promise<{
  receiptsUploaded: number;
  pricesContributed: number;
  pointsTotal: number;
  badgeLevel: string;
} | null> {
  const { data: { user } } = await supabase.auth.getUser();
  if (!user) return null;

  const { data } = await supabase
    .from('user_price_stats')
    .select('receipts_uploaded, prices_contributed, points_total, badge_level')
    .eq('user_id', user.id)
    .single();

  if (!data) return null;
  return {
    receiptsUploaded:   data.receipts_uploaded,
    pricesContributed:  data.prices_contributed,
    pointsTotal:        data.points_total,
    badgeLevel:         data.badge_level,
  };
}

// ── 6. Snelste prijs ophalen voor vergelijking ────────────────
// Gebruikt door fetchRealPrice() in App.tsx als eerste stap
// Volgorde: Supabase DB → AH API → Jumbo API → schatting
export async function getBestPrice(
  productName: string,
  storeChain:  string,
  storeCity?:  string,
): Promise<PriceLookupResult | null> {
  return lookupPrice(productName, storeChain, storeCity);
}

export default {
  lookupPrice,
  reportPrice,
  uploadReceipt,
  flushOfflineQueue,
  getUserPriceStats,
  getBestPrice,
};
