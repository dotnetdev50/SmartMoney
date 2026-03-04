//Api calls are now mocked by static JSON files in the public/data directory. 
// The types below are not currently used, but could be helpful for future development 
// when we switch back to real API calls.
export type ParticipantDto = {
  name: string;
  bias: number;
  label?: string; // optional for JSON mode
};

export type MarketTodayResponse = {
  index: string;
  date: string;
  final_score: number;
  regime: string;
  shock_score?: number;
  participants: ParticipantDto[];
  explanation?: string;

  // PCR and VIX (added for PCR feature)
  pcr?: number | null;
  vix?: number | null;

  // Extended PCR fields from NSE PR file
  pcr_volume?: number | null;
  banknifty_pcr?: number | null;
  banknifty_pcr_volume?: number | null;

  // Optional if you keep the old API contract too
  final_Score?: number;
  bias_Label?: string;
  strength?: string;
  final_score_raw?: number;
};

export type MarketHistoryPoint = {
  date: string;
  final_score: number;
  regime: string;
};

const JSON_BASE = import.meta.env.VITE_JSON_BASE_URL ?? "/data";

async function jsonGet<T>(file: string): Promise<T> {
  const res = await fetch(`${JSON_BASE}/${file}`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json() as Promise<T>;
}

export const api = {
  marketToday: () => jsonGet<MarketTodayResponse>("market_today.json"),
  marketHistory: () => jsonGet<MarketHistoryPoint[]>("market_history_30.json"),
};

// export type ParticipantDto = {
//   name: string;
//   bias: number;
//   label: string;
// };

// export type MarketTodayResponse = {
//   index: string;
//   date: string;
//   asOfDate?: string;
//   dateasof?: string;
//   final_Score: number;
//   bias_Label: string;
//   strength: string;
//   regime: string;
//   shock_Score: number;
//   participants: ParticipantDto[];
//   explanation: string;
// };

// export type MarketHistoryPoint = {
//   date: string;
//   final_score: number;
//   regime: string;
// };

// const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "";

// async function httpGet<T>(path: string): Promise<T> {
//   const res = await fetch(`${BASE_URL}${path}`);
//   if (!res.ok) {
//     const txt = await res.text();
//     throw new Error(txt || `HTTP ${res.status}`);
//   }
//   return res.json() as Promise<T>;
// }

// export const api = {
//   marketToday: () => httpGet<MarketTodayResponse>("/api/market/today"),
//   marketHistory: (days = 30) => httpGet<MarketHistoryPoint[]>(`/api/market/history?days=${days}`),
// };
