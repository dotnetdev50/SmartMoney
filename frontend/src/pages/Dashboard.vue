<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { api, type MarketHistoryPoint, type MarketTodayResponse } from "@/services/api";

const loading = ref(true);
const error = ref<string | null>(null);
const isDark = ref(document.documentElement.classList.contains("dark"));

const today = ref<MarketTodayResponse | null>(null);
const history = ref<MarketHistoryPoint[]>([]);

function formatDate(value?: string | null) {
  if (!value) return "-";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}

const signalDate = computed(() => formatDate(today.value?.date));

// JSON mode doesn’t necessarily provide asOfDate; we fall back to date
const asOfDate = computed(() => formatDate(today.value?.date));

const historyCount = computed(() => history.value.length);

const historyDelta = computed(() => {
  if (history.value.length < 2) return null;
  const firstPoint = history.value[0];
  const lastPoint = history.value[history.value.length - 1];
  if (!firstPoint || !lastPoint) return null;
  return lastPoint.final_score - firstPoint.final_score;
});

const trendLabel = computed(() => {
  const delta = historyDelta.value;
  if (delta === null) return "Flat";
  if (delta > 0) return "Improving";
  if (delta < 0) return "Weakening";
  return "Flat";
});

const scoreColorClass = computed(() => {
  if (!today.value) return "text-gray-900 dark:text-gray-100";
  const v = today.value.final_score;
  if (v >= 40) return "text-green-700 dark:text-green-400";
  if (v <= -40) return "text-red-700 dark:text-red-400";
  return "text-gray-900 dark:text-gray-100";
});

const scoreBadgeClass = computed(() => {
  if (!today.value)
    return "bg-gray-100 text-gray-700 border-gray-200 dark:bg-gray-800 dark:text-gray-200 dark:border-gray-700";
  const v = today.value.final_score;
  if (v >= 40)
    return "bg-green-100 text-green-800 border-green-200 dark:bg-green-500/15 dark:text-green-300 dark:border-green-500/30";
  if (v <= -40)
    return "bg-red-100 text-red-800 border-red-200 dark:bg-red-500/15 dark:text-red-300 dark:border-red-500/30";
  return "bg-gray-100 text-gray-700 border-gray-200 dark:bg-gray-800 dark:text-gray-200 dark:border-gray-700";
});

const regimeBadgeClass = computed(() => {
  if (!today.value)
    return "bg-gray-100 text-gray-700 border-gray-200 dark:bg-gray-800 dark:text-gray-200 dark:border-gray-700";
  return today.value.regime === "SHOCK"
    ? "bg-orange-100 text-orange-800 border-orange-200 dark:bg-orange-500/15 dark:text-orange-300 dark:border-orange-500/30"
    : "bg-gray-100 text-gray-700 border-gray-200 dark:bg-gray-800 dark:text-gray-200 dark:border-gray-700";
});

const shockBadgeClass = computed(() => {
  if (!today.value)
    return "bg-gray-100 text-gray-700 border-gray-200 dark:bg-gray-800 dark:text-gray-200 dark:border-gray-700";
  const v = today.value.shock_score ?? 0;
  if (v >= 25)
    return "bg-orange-100 text-orange-800 border-orange-200 dark:bg-orange-500/15 dark:text-orange-300 dark:border-orange-500/30";
  if (v >= 10)
    return "bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-500/15 dark:text-amber-300 dark:border-amber-500/30";
  return "bg-green-100 text-green-800 border-green-200 dark:bg-green-500/15 dark:text-green-300 dark:border-green-500/30";
});

type ParticipantKey = "FII" | "DII" | "PRO" | "RETAIL";

type ParticipantRow = {
  key: ParticipantKey;
  name: string;
  bias: number;
  label: string;
  hasData: boolean;
};

const participantOrder: ParticipantKey[] = ["FII", "DII", "PRO", "RETAIL"];

function participantDisplayName(key: ParticipantKey) {
  return key === "RETAIL" ? "Retail" : key;
}

function normalizeParticipantName(name: string): ParticipantKey | null {
  const normalized = name.trim().toUpperCase();
  if (normalized.includes("FII")) return "FII";
  if (normalized.includes("DII")) return "DII";
  if (normalized === "PRO" || normalized.includes("PROP") || normalized.includes("PRO")) return "PRO";
  if (normalized.includes("RETAIL") || normalized.includes("CLIENT") || normalized.includes("INDIVIDUAL")) return "RETAIL";
  return null;
}

function fallbackLabelFromBias(bias: number) {
  const abs = Math.abs(bias);
  if (abs >= 2.0) return bias > 0 ? "Strong Bullish" : "Strong Bearish";
  if (abs >= 1.0) return bias > 0 ? "Bullish" : "Bearish";
  if (abs >= 0.4) return bias > 0 ? "Mild Bullish" : "Mild Bearish";
  return "Neutral";
}

const participantRows = computed<ParticipantRow[]>(() => {
  const rows = new Map<ParticipantKey, ParticipantRow>();

  if (today.value) {
    for (const participant of today.value.participants ?? []) {
      const key = normalizeParticipantName(participant.name);
      if (!key || rows.has(key)) continue;

      const bias = participant.bias ?? 0;
      const label = participant.label ?? fallbackLabelFromBias(bias);

      rows.set(key, {
        key,
        name: participantDisplayName(key),
        bias,
        label,
        hasData: true,
      });
    }
  }

  return participantOrder.map((key) =>
    rows.get(key) ?? {
      key,
      name: participantDisplayName(key),
      bias: 0,
      label: "-",
      hasData: false,
    },
  );
});

const topParticipant = computed(() => {
  const available = participantRows.value.filter((row) => row.hasData);
  if (available.length === 0) return null;
  return [...available].sort((a, b) => Math.abs(b.bias) - Math.abs(a.bias))[0] ?? null;
});

const historyDeltaClass = computed(() => {
  const delta = historyDelta.value;
  if (delta === null) return "text-gray-600 dark:text-gray-300";
  if (delta > 0) return "text-green-700 dark:text-green-400";
  if (delta < 0) return "text-red-700 dark:text-red-400";
  return "text-gray-600 dark:text-gray-300";
});

const historyRange = computed(() => {
  if (history.value.length === 0) return "No history data";
  return `${history.value[0]?.date} → ${history.value[history.value.length - 1]?.date}`;
});

function fmtScore(n: number) {
  return (Math.round(n * 10) / 10).toFixed(1);
}

function fmtPcr(v: number | null | undefined): string {
  if (v == null) return "—";
  return v.toFixed(2);
}

function fmtVix(v: number | null | undefined): string {
  if (v == null) return "—";
  return v.toFixed(2);
}

function pcrLabel(v: number | null | undefined): string {
  if (v == null) return "";
  if (v >= 1.3) return "Bullish";
  if (v >= 0.8) return "Neutral";
  return "Bearish";
}

function pcrLabelClass(v: number | null | undefined): string {
  if (v == null) return "text-gray-500 dark:text-gray-400";
  if (v >= 1.3) return "text-green-700 dark:text-green-400";
  if (v >= 0.8) return "text-gray-600 dark:text-gray-300";
  return "text-red-700 dark:text-red-400";
}

function participantToneClass(value: number) {
  if (value > 0) return "text-green-700 dark:text-green-400";
  if (value < 0) return "text-red-700 dark:text-red-400";
  return "text-gray-700 dark:text-gray-200";
}

function participantBarClass(value: number) {
  if (value > 0) return "bg-green-600";
  if (value < 0) return "bg-red-600";
  return "bg-gray-500";
}

function participantBarWidth(value: number) {
  const width = Math.min(100, Math.max(8, Math.abs(value)));
  return `${width}%`;
}

const scoreMeterWidth = computed(() => {
  if (!today.value) return "0%";
  return `${Math.min(50, Math.abs(today.value.final_score) / 2)}%`;
});

async function load() {
  loading.value = true;
  error.value = null;

  try {
    const [t, h] = await Promise.all([api.marketToday(), api.marketHistory()]);
    today.value = t;
    history.value = h;
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : "Failed to load data";
  } finally {
    loading.value = false;
  }
}

function setTheme(dark: boolean) {
  document.documentElement.classList.toggle("dark", dark);
  localStorage.setItem("theme", dark ? "dark" : "light");
  isDark.value = dark;
}

function toggleTheme() {
  setTheme(!isDark.value);
}

onMounted(load);

// Sparkline points
const points = computed(() => {
  if (history.value.length === 0) return "";
  const ys = history.value.map((p: MarketHistoryPoint) => p.final_score);

  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  const rangeY = Math.max(1e-6, maxY - minY);

  const width = 100;
  const height = 36;
  const pad = 2;

  const xScale = (i: number) =>
    pad + (i * (width - pad * 2)) / Math.max(1, ys.length - 1);

  const yScale = (v: number) =>
    pad + (height - pad * 2) * (1 - (v - minY) / rangeY);

  return ys
    .map((v: number, i: number) => `${xScale(i).toFixed(2)},${yScale(v).toFixed(2)}`)
    .join(" ");
});
</script>

<template>
  <div
    class="dashboard-shell flex min-h-dvh items-stretch justify-center overflow-y-auto bg-gray-100 text-gray-900 dark:bg-gray-950 dark:text-gray-100 lg:h-dvh lg:overflow-hidden"
  >
    <div class="dashboard-inner flex w-full max-w-[1280px] flex-col gap-2 px-3 py-2 sm:px-4 sm:py-3 lg:h-full">
      <header class="flex shrink-0 flex-col gap-1.5 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <p class="text-xs font-semibold uppercase tracking-wider text-indigo-600 dark:text-indigo-400">Dashboard</p>
          <h1 class="text-xl font-bold tracking-tight sm:text-2xl">Smart Money - NIFTY Bias</h1>
          <p class="text-xs text-gray-600 dark:text-gray-300 sm:text-sm" v-if="today">
            As Of Date: <span class="font-semibold text-gray-800 dark:text-gray-100">{{ asOfDate }}</span>
            <span class="mx-2 text-gray-300 dark:text-gray-600">•</span>
            Signal Date: <span class="font-semibold text-gray-800 dark:text-gray-100">{{ signalDate }}</span>
          </p>
        </div>

        <div class="flex items-center gap-2">
          <button
            class="inline-flex h-10 min-w-[112px] items-center justify-center rounded-lg border border-gray-300 bg-white px-4 text-sm font-medium text-gray-700 hover:bg-gray-100 dark:border-gray-700 dark:bg-gray-900 dark:text-gray-100 dark:hover:bg-gray-800"
            @click="toggleTheme"
            type="button"
          >
            {{ isDark ? "Light mode" : "Dark mode" }}
          </button>
          <button
            class="inline-flex h-10 min-w-[112px] items-center justify-center rounded-lg border border-gray-300 bg-white px-4 text-sm font-medium text-gray-700 hover:bg-gray-100 disabled:cursor-not-allowed disabled:opacity-60 dark:border-gray-700 dark:bg-gray-900 dark:text-gray-100 dark:hover:bg-gray-800"
            @click="load"
            :disabled="loading"
          >
            Refresh
          </button>
        </div>
      </header>

      <div
        v-if="error"
        class="shrink-0 rounded-xl border border-red-200 bg-red-50 p-2 text-sm text-red-800 dark:border-red-500/30 dark:bg-red-500/15 dark:text-red-200"
      >
        {{ error }}
      </div>

      <section v-if="!loading && today" class="dashboard-kpi-grid grid shrink-0 grid-cols-2 gap-2 lg:grid-cols-4">
        <article
          class="dashboard-card rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:min-h-[92px]"
        >
          <p class="text-xs font-medium uppercase tracking-wider text-gray-500 dark:text-gray-400">Final Score</p>
          <p :class="['mt-1 text-2xl font-bold leading-none', scoreColorClass]">
            {{ fmtScore(today.final_score) }}
          </p>
          <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">
            {{ (today.strength ?? "—") }} {{ (today.bias_Label ?? "") }}
          </p>
        </article>

        <article
          class="dashboard-card rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:min-h-[92px]"
        >
          <p class="text-xs font-medium uppercase tracking-wider text-gray-500 dark:text-gray-400">Regime</p>
          <p class="mt-1 text-2xl font-semibold leading-none text-gray-900 dark:text-gray-100">{{ today.regime }}</p>
          <span :class="['mt-1 inline-flex rounded-full border px-2 py-0.5 text-xs font-semibold', regimeBadgeClass]">
            Live Regime
          </span>
        </article>

        <article
          class="dashboard-card rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:min-h-[92px]"
        >
          <p class="text-xs font-medium uppercase tracking-wider text-gray-500 dark:text-gray-400">30D Trend</p>
          <p :class="['mt-1 text-2xl font-semibold leading-none', historyDeltaClass]">{{ trendLabel }}</p>
          <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">{{ historyCount }} points tracked</p>
        </article>

        <article
          class="dashboard-card rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:min-h-[92px]"
        >
          <p class="text-xs font-medium uppercase tracking-wider text-gray-500 dark:text-gray-400">As Of Date</p>
          <p class="mt-1 text-2xl font-semibold leading-none text-gray-900 dark:text-gray-100">{{ asOfDate }}</p>
          <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">Data publishing timestamp</p>
        </article>
      </section>

      <div v-if="loading" class="space-y-2">
        <div class="h-48 animate-pulse rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900"></div>
        <div class="grid gap-2 md:grid-cols-2">
          <div class="h-60 animate-pulse rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900"></div>
          <div class="h-60 animate-pulse rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900"></div>
        </div>
      </div>

      <template v-else>
        <div
          v-if="today"
          class="dashboard-main-grid grid gap-2 lg:min-h-0 lg:flex-1 lg:grid-rows-[minmax(0,0.86fr)_minmax(0,0.8fr)_minmax(0,1.12fr)]"
        >
          <section class="grid gap-2 lg:min-h-0 lg:grid-cols-12">
            <article
              class="overflow-hidden rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:col-span-7 lg:h-full lg:min-h-0"
            >
              <p class="text-xs font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">Headline Signal</p>
              <div class="mt-1.5 flex flex-wrap items-end gap-2">
                <div :class="['text-4xl font-bold leading-none', scoreColorClass]">
                  {{ fmtScore(today.final_score) }}
                </div>
                <span :class="['inline-flex rounded-full border px-2.5 py-0.5 text-sm font-semibold', scoreBadgeClass]">
                  {{ (today.strength ?? "—") }} {{ (today.bias_Label ?? "") }}
                </span>
              </div>
              <div class="mt-1.5 flex flex-wrap items-center gap-2">
                <span :class="['rounded-full border px-2 py-0.5 text-xs font-semibold', regimeBadgeClass]">
                  Regime: {{ today.regime }}
                </span>
                <span :class="['rounded-full border px-2 py-0.5 text-xs font-semibold', shockBadgeClass]">
                  Shock Score: {{ fmtScore(today.shock_score ?? 0) }}
                </span>
              </div>

              <div class="mt-2">
                <div class="mb-1 flex justify-between text-xs font-medium text-gray-500 dark:text-gray-400">
                  <span>Bearish</span>
                  <span>Neutral</span>
                  <span>Bullish</span>
                </div>
                <div class="relative h-2 rounded-full bg-gray-200 dark:bg-gray-800">
                  <div class="absolute left-1/2 top-0 h-full w-px -translate-x-1/2 bg-gray-400 dark:bg-gray-600"></div>
                  <div
                    :class="[
                      'absolute top-0 h-full rounded-full',
                      today.final_score >= 0 ? 'left-1/2 bg-green-600' : 'right-1/2 bg-red-600'
                    ]"
                    :style="{ width: scoreMeterWidth }"
                  ></div>
                </div>
              </div>
            </article>

            <article
              class="flex flex-col overflow-hidden rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:col-span-5 lg:h-full lg:min-h-0"
            >
              <h2 class="text-base font-semibold">Explanation</h2>
              <p class="mt-1 max-h-[112px] overflow-hidden text-xs leading-5 text-gray-800 dark:text-gray-200 sm:text-sm">
                {{ today.explanation ?? "Explanation will appear here once enabled." }}
              </p>
              <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">Rule-based explanation (V1). No AI.</p>
            </article>
          </section>

          <section class="grid gap-2 lg:min-h-0 lg:grid-cols-12">
            <article
              class="flex flex-col overflow-hidden rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:col-span-7 lg:h-full lg:min-h-0"
            >
              <div class="mb-2 flex flex-wrap items-center justify-between gap-2">
                <h2 class="text-base font-semibold">30-Day Trend</h2>
                <p :class="['text-sm font-semibold', historyDeltaClass]">
                  Change: {{ historyDelta !== null ? fmtScore(historyDelta) : "-" }}
                </p>
              </div>
              <div class="min-h-[100px] rounded-lg border border-gray-200 bg-white p-1.5 dark:border-gray-800 dark:bg-gray-900 lg:min-h-0 lg:flex-1">
                <svg viewBox="0 0 100 36" preserveAspectRatio="none" class="h-full min-h-[72px] w-full text-gray-900 dark:text-gray-200">
                  <polyline :points="points" fill="none" stroke="currentColor" stroke-width="1.6" />
                </svg>
              </div>
              <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">{{ historyRange }}</p>
            </article>

            <article
              class="overflow-hidden rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:col-span-5 lg:h-full lg:min-h-0"
            >
              <p class="text-xs font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">Quick Facts</p>
              <dl class="mt-2 grid grid-cols-2 gap-x-6 gap-y-2">
                <div class="space-y-2">
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400">Index</dt>
                    <dd class="text-sm font-semibold leading-tight text-gray-900 dark:text-gray-100">{{ today.index }}</dd>
                  </div>
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400">Top Driver</dt>
                    <dd class="text-sm font-semibold leading-tight text-gray-900 dark:text-gray-100">
                      {{ topParticipant ? topParticipant.name : "-" }}
                      <span
                        v-if="topParticipant"
                        :class="['ml-1 text-xs font-medium', participantToneClass(topParticipant.bias)]"
                      >
                        {{ fmtScore(topParticipant.bias) }} ({{ topParticipant.label }})
                      </span>
                    </dd>
                  </div>
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400" title="Put-Call Ratio by Open Interest">PCR OI (NIFTY)</dt>
                    <dd class="text-sm font-semibold leading-tight text-gray-900 dark:text-gray-100">
                      <template v-if="today.pcr != null">
                        {{ fmtPcr(today.pcr) }}
                        <span :class="['ml-1 text-xs font-medium', pcrLabelClass(today.pcr)]">
                          {{ pcrLabel(today.pcr) }}
                        </span>
                      </template>
                      <span
                        v-else
                        class="inline-flex items-center gap-1 rounded-full border border-amber-300 bg-amber-50 px-1.5 py-0.5 text-xs font-medium text-amber-700 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-400"
                        title="PCR data not yet published by NSE for today"
                      >
                        Unavailable
                      </span>
                    </dd>
                  </div>
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400" title="Put-Call Ratio by traded volume (contracts)">PCR Vol (NIFTY)</dt>
                    <dd class="text-sm font-semibold leading-tight text-gray-900 dark:text-gray-100">
                      <template v-if="today.pcr_volume != null">
                        {{ fmtPcr(today.pcr_volume) }}
                        <span :class="['ml-1 text-xs font-medium', pcrLabelClass(today.pcr_volume)]">
                          {{ pcrLabel(today.pcr_volume) }}
                        </span>
                      </template>
                      <span
                        v-else
                        class="inline-flex items-center gap-1 rounded-full border border-amber-300 bg-amber-50 px-1.5 py-0.5 text-xs font-medium text-amber-700 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-400"
                        title="PCR Volume data not yet published by NSE for today"
                      >
                        Unavailable
                      </span>
                    </dd>
                  </div>
                </div>

                <div class="space-y-2">
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400">Data Date</dt>
                    <dd class="text-sm font-semibold text-gray-900 dark:text-gray-100">{{ signalDate }}</dd>
                  </div>
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400">India VIX</dt>
                    <dd class="text-sm font-semibold text-gray-900 dark:text-gray-100">
                      <template v-if="today.vix != null">
                        {{ fmtVix(today.vix) }}
                      </template>
                      <span
                        v-else
                        class="inline-flex items-center gap-1 rounded-full border border-amber-300 bg-amber-50 px-1.5 py-0.5 text-xs font-medium text-amber-700 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-400"
                        title="VIX data not yet published by NSE for today"
                      >
                        Unavailable
                      </span>
                    </dd>
                  </div>
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400" title="Put-Call Ratio by Open Interest">PCR OI (BANKNIFTY)</dt>
                    <dd class="text-sm font-semibold leading-tight text-gray-900 dark:text-gray-100">
                      <template v-if="today.banknifty_pcr != null">
                        {{ fmtPcr(today.banknifty_pcr) }}
                        <span :class="['ml-1 text-xs font-medium', pcrLabelClass(today.banknifty_pcr)]">
                          {{ pcrLabel(today.banknifty_pcr) }}
                        </span>
                      </template>
                      <span
                        v-else
                        class="inline-flex items-center gap-1 rounded-full border border-amber-300 bg-amber-50 px-1.5 py-0.5 text-xs font-medium text-amber-700 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-400"
                        title="BANKNIFTY PCR data not yet published by NSE for today"
                      >
                        Unavailable
                      </span>
                    </dd>
                  </div>
                  <div>
                    <dt class="text-xs text-gray-500 dark:text-gray-400" title="Put-Call Ratio by traded volume (contracts)">PCR Vol (BANKNIFTY)</dt>
                    <dd class="text-sm font-semibold leading-tight text-gray-900 dark:text-gray-100">
                      <template v-if="today.banknifty_pcr_volume != null">
                        {{ fmtPcr(today.banknifty_pcr_volume) }}
                        <span :class="['ml-1 text-xs font-medium', pcrLabelClass(today.banknifty_pcr_volume)]">
                          {{ pcrLabel(today.banknifty_pcr_volume) }}
                        </span>
                      </template>
                      <span
                        v-else
                        class="inline-flex items-center gap-1 rounded-full border border-amber-300 bg-amber-50 px-1.5 py-0.5 text-xs font-medium text-amber-700 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-400"
                        title="BANKNIFTY PCR Volume data not yet published by NSE for today"
                      >
                        Unavailable
                      </span>
                    </dd>
                  </div>
                </div>
              </dl>
            </article>
          </section>

          <section class="overflow-hidden rounded-xl border border-gray-200 bg-white p-2.5 shadow-sm dark:border-gray-800 dark:bg-gray-900 lg:h-full lg:min-h-0">
            <div class="mb-1.5 flex items-center justify-between">
              <h2 class="text-base font-semibold">FII / DII / PRO / Retail</h2>
              <p class="text-xs text-gray-500 dark:text-gray-400">Standard participant view</p>
            </div>

            <div>
              <table class="min-w-full table-fixed text-sm">
                <colgroup>
                  <col class="w-[26%]" />
                  <col class="w-[18%]" />
                  <col class="w-[28%]" />
                  <col class="w-[28%]" />
                </colgroup>
                <thead class="text-left text-xs uppercase tracking-wider text-gray-500 dark:text-gray-400">
                  <tr class="border-b border-gray-200 dark:border-gray-800">
                    <th class="py-1 pr-3">Participant</th>
                    <th class="py-1 pr-3 text-right">Bias</th>
                    <th class="py-1 pr-3">Influence</th>
                    <th class="py-1 pr-0">Label</th>
                  </tr>
                </thead>
                <tbody>
                  <tr
                    v-for="p in participantRows"
                    :key="p.key"
                    class="border-b border-gray-100 last:border-b-0 dark:border-gray-800"
                  >
                    <td class="py-1 pr-3 font-medium text-gray-900 dark:text-gray-100">{{ p.name }}</td>
                    <td :class="['py-1 pr-3 text-right font-semibold', participantToneClass(p.bias)]">
                      {{ p.hasData ? fmtScore(p.bias) : '-' }}
                    </td>
                    <td class="py-1 pr-3">
                      <div class="h-2 w-full max-w-28 rounded-full bg-gray-200 dark:bg-gray-800">
                        <div
                          :class="['h-2 rounded-full', participantBarClass(p.bias)]"
                          :style="{ width: p.hasData ? participantBarWidth(p.bias) : '0%' }"
                        ></div>
                      </div>
                    </td>
                    <td
                      :class="[
                        'py-1 pr-0',
                        p.hasData ? 'text-gray-700 dark:text-gray-300' : 'text-gray-500 dark:text-gray-400'
                      ]"
                    >
                      {{ p.label }}
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </section>
        </div>

        <div
          v-else
          class="rounded-2xl border border-gray-200 bg-white p-6 text-sm text-gray-600 shadow-sm dark:border-gray-800 dark:bg-gray-900 dark:text-gray-300"
        >
          No market data available.
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.dashboard-shell {
  --dash-gap: clamp(0.45rem, 0.65vw, 0.75rem);
  --dash-pad: clamp(0.5rem, 0.7vw, 0.9rem);
  --dash-kpi-min: clamp(86px, 11.5vh, 110px);
  --dash-headline-size: clamp(2rem, 3.5vh, 2.6rem);
  --dash-kpi-size: clamp(1.55rem, 2.5vh, 2rem);
  --dash-chart-min: clamp(64px, 10vh, 92px);
  --dash-expl-max: clamp(88px, 14vh, 128px);
}

.dashboard-shell .dashboard-kpi-grid,
.dashboard-shell .dashboard-main-grid {
  gap: var(--dash-gap);
}

.dashboard-shell .dashboard-card {
  min-height: var(--dash-kpi-min);
  padding: var(--dash-pad);
}

.dashboard-shell .text-4xl {
  font-size: var(--dash-headline-size);
}

.dashboard-shell .text-2xl {
  font-size: var(--dash-kpi-size);
}

.dashboard-shell .min-h-\[72px\] {
  min-height: var(--dash-chart-min);
}

.dashboard-shell .max-h-\[112px\] {
  max-height: var(--dash-expl-max);
}

@media (min-width: 1024px) and (max-height: 860px) {
  .dashboard-shell {
    --dash-gap: clamp(0.4rem, 0.55vw, 0.65rem);
    --dash-pad: clamp(0.42rem, 0.55vw, 0.72rem);
    --dash-kpi-min: clamp(78px, 10.5vh, 96px);
    --dash-headline-size: clamp(1.8rem, 3.15vh, 2.2rem);
    --dash-kpi-size: clamp(1.35rem, 2.2vh, 1.75rem);
    --dash-chart-min: clamp(56px, 8.6vh, 76px);
    --dash-expl-max: clamp(76px, 11.5vh, 104px);
  }

  .dashboard-main-grid {
    grid-template-rows: minmax(0, 0.84fr) minmax(0, 0.78fr) minmax(0, 1.1fr);
  }
}

@media (min-width: 1024px) and (max-height: 760px) {
  .dashboard-main-grid {
    grid-template-rows: minmax(0, 0.8fr) minmax(0, 0.74fr) minmax(0, 1.08fr);
  }

  .dashboard-shell .text-xs {
    line-height: 1.1;
  }
}

/* Tall screens (≥960px height): switch to scrollable layout with capped card heights */
@media (min-width: 1024px) and (min-height: 960px) {
  .dashboard-shell {
    height: auto;
    overflow-y: auto;
    align-items: flex-start;
  }

  .dashboard-inner {
    height: auto;
  }

  .dashboard-main-grid {
    flex: none;
    grid-template-rows: minmax(160px, 220px) minmax(195px, 255px) minmax(170px, 210px);
  }
}

/* Extra-wide screens (≥1536px): expand content max-width */
@media (min-width: 1536px) {
  .dashboard-inner {
    max-width: 1440px;
  }
}
</style>
