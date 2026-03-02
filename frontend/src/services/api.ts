export type ParticipantDto = {
  name: string;
  bias: number;
  label: string;
};

export type MarketTodayResponse = {
  index: string;
  date: string;
  asOfDate?: string;
  dateasof?: string;
  final_Score: number;
  bias_Label: string;
  strength: string;
  regime: string;
  shock_Score: number;
  participants: ParticipantDto[];
  explanation: string;
};

export type MarketHistoryPoint = {
  date: string;
  final_score: number;
  regime: string;
};

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "";

async function httpGet<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE_URL}${path}`);
  if (!res.ok) {
    const txt = await res.text();
    throw new Error(txt || `HTTP ${res.status}`);
  }
  return res.json() as Promise<T>;
}

export const api = {
  marketToday: () => httpGet<MarketTodayResponse>("/api/market/today"),
  marketHistory: (days = 30) => httpGet<MarketHistoryPoint[]>(`/api/market/history?days=${days}`),
};
