# SmartMoney

Dashboard for Traders. Make better decisions.

## Architecture

The project is split into two parts:

- **Backend** (`backend/`) — A .NET 8 C# batch job that ingests NSE participant OI data, computes market bias, and writes JSON output consumed by the frontend.
- **Frontend** (`frontend/`) — A Vite + Vue 3 + TypeScript dashboard that reads the exported JSON files and renders the data.

### Data Flow

```
Backend Job (C#) → frontend/public/data/*.json → npm run build → dist/data/ → GitHub Pages
```

The backend job is run by the GitHub Actions workflow (`.github/workflows/pages.yml`) every weekday at 9:40 PM IST.

---

## PCR and VIX Integration

### Put-Call Ratio (PCR)

PCR measures market sentiment by comparing total put open interest to call open interest for NIFTY index options.

**Source:** NSE FO Bhavcopy ZIP — `https://nsearchives.nseindia.com/content/historical/DERIVATIVES/{YYYY}/{MMM}/fo{DD}{MMM}{YYYY}bhav.csv.zip`

**Service:** `backend/SmartMoney.Application/Services/FoBhavCopyService.cs`

- Downloads the daily FO bhavcopy ZIP (e.g. `fo04MAR2026bhav.csv.zip`).
- Extracts the CSV inside the ZIP.
- Filters rows where `INSTRUMENT = OPTIDX` and `SYMBOL = NIFTY`.
- Computes `PCR = Sum(OPEN_INT for PE) / Sum(OPEN_INT for CE)`.
- Returns `null` on 404 (holiday or data not yet published) or parse errors.

**PCR interpretation:**

| Value    | Signal   |
|----------|----------|
| ≥ 1.3    | Bullish (high put buying = hedging, market likely to rise) |
| 0.8–1.3  | Neutral  |
| < 0.8    | Bearish (low put buying = complacency) |

---

### India VIX

VIX measures implied volatility (market fear/uncertainty) derived from NIFTY options prices.

**Primary source:** NSE JSON API

```
GET https://www.nseindia.com/api/historicalOR/vixhistory?from=DD-MM-YYYY&to=DD-MM-YYYY
```

> **Important:** The NSE website uses Akamai bot-protection. The API requires valid session cookies.
> The backend visits the NSE homepage first (`GET https://www.nseindia.com/`) using a cookie-aware HTTP client to prime the session, then calls the API. Both requests share the same `CookieContainer`.

**Fallback source:** Full-history CSV from NSE archives

```
https://nsearchives.nseindia.com/content/indices/hist_vix_data.csv
```

**Service:** `backend/SmartMoney.Application/Services/VixFetchService.cs`

- **Step 1:** Creates a short-lived `HttpClient` with `HttpClientHandler { UseCookies = true }`.
- **Step 2:** GETs the NSE homepage to obtain session cookies.
- **Step 3:** GETs the VIX API with those cookies; parses `EOD_CLOSE_INDEX_VAL` from the JSON response.
- **Fallback:** If the API fails (HTTP error / no data), downloads the archives CSV and parses the matching date row.

---

### Retry Logic

PCR/VIX fetching is retried by the job (configured in `NseJobOptions`):

| Option               | Default | Description                    |
|----------------------|---------|-------------------------------|
| `PcrVixMaxRetries`   | 1       | Number of fetch attempts       |
| `PcrVixRetryMinutes` | 2       | Minutes between retries        |

NSE typically publishes bhavcopy data between 6–8 PM IST; the job runs at 9:40 PM IST to allow sufficient time.

---

### Dashboard UI

The dashboard (`frontend/src/pages/Dashboard.vue`) displays:

- **PCR (NIFTY):** Value with Bullish / Neutral / Bearish label, or an amber **Unavailable** badge if data is null.
- **India VIX:** Closing value, or an amber **Unavailable** badge if data is null.

The amber "Unavailable" badge appears when the backend job ran but could not obtain the value (e.g., holiday, data not yet published, or NSE API errors).

---

## Development

### Backend

```bash
cd backend
dotnet restore
dotnet build -c Release
dotnet run -c Release --project SmartMoney.Job/SmartMoney.Job.csproj
```

### Frontend

```bash
cd frontend
npm install
npm run dev       # development server
npm run build     # production build → dist/
```

### CI / GitHub Actions

The workflow in `.github/workflows/pages.yml`:

1. Restores and builds the .NET backend.
2. Runs `SmartMoney.Job` — ingests participant OI, computes bias, fetches PCR/VIX, writes `frontend/public/data/*.json`.
3. Commits the updated JSON files back to the repo.
4. Builds the Vue frontend (`npm run build`).
5. Deploys `frontend/dist/` to GitHub Pages.
