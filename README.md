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

PCR measures market sentiment by comparing total put open interest (or volume) to call open interest (or volume) for index options.

#### Primary Source: NSE PR File (Options Bhavcopy)

The primary PCR source is the **NSE PR file** (`pr{DDMMYYYY}.zip`) which contains end-of-day options data for all F&O instruments.

**Source URL pattern:** `https://nsearchives.nseindia.com/content/fo/pr{DDMMYYYY}.zip`

Example: `https://nsearchives.nseindia.com/content/fo/pr04032026.zip` contains `pr04032026.csv`.

**Service:** `backend/SmartMoney.Application/Services/PrPcrService.cs`

The CSV inside the ZIP has the following columns:
```
SYMBOL, EXPIRY_DT, OPTION_TYP, STRIKE_PR, OPEN, HIGH, LOW, CLOSE, SETTLE_PR,
CONTRACTS, VAL_INLAKH, OPEN_INT, CHG_IN_OI, TIMESTAMP
```

**Calculation:**
- Filter rows by `SYMBOL` (e.g. `NIFTY`, `BANKNIFTY`)
- Sum `OPEN_INT` for `OPTION_TYP = PE` → Put OI
- Sum `OPEN_INT` for `OPTION_TYP = CE` → Call OI
- Sum `CONTRACTS` for `OPTION_TYP = PE` → Put Volume
- Sum `CONTRACTS` for `OPTION_TYP = CE` → Call Volume

**Formulae:**
```
PCR (OI)     = Total Put OI     / Total Call OI
PCR (Volume) = Total Put Volume / Total Call Volume
```

**Computed values (per symbol):**

| JSON field              | Description                         |
|-------------------------|-------------------------------------|
| `pcr`                   | NIFTY PCR (Open Interest)           |
| `pcr_volume`            | NIFTY PCR (Volume / Contracts)      |
| `banknifty_pcr`         | BANKNIFTY PCR (Open Interest)       |
| `banknifty_pcr_volume`  | BANKNIFTY PCR (Volume / Contracts)  |

#### Fallback Source: FO Bhavcopy (NIFTY OI PCR only)

If the PR file is unavailable (holiday, 404, or data not yet published), the service falls back to the **FO Bhavcopy ZIP** for the NIFTY OI PCR value only.

**Source URL pattern:** `https://nsearchives.nseindia.com/content/historical/DERIVATIVES/{YYYY}/{MMM}/fo{DD}{MMM}{YYYY}bhav.csv.zip`

**Service:** `backend/SmartMoney.Application/Services/FoBhavCopyService.cs`

> **Note:** Always prefer the PR file (`pr{DDMMYYYY}.zip`) for PCR calculations. The FO Bhavcopy fallback only provides NIFTY OI PCR and does not cover Volume PCR or BANKNIFTY.

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

### Dashboard UI

The dashboard (`frontend/src/pages/Dashboard.vue`) displays under **Quick Facts**:

- **PCR OI (NIFTY):** NIFTY Put-Call Ratio by Open Interest with Bullish / Neutral / Bearish label.
- **PCR Vol (NIFTY):** NIFTY Put-Call Ratio by traded volume (contracts) with Bullish / Neutral / Bearish label.
- **PCR OI (BANKNIFTY):** BANKNIFTY Put-Call Ratio by Open Interest with Bullish / Neutral / Bearish label.
- **PCR Vol (BANKNIFTY):** BANKNIFTY Put-Call Ratio by traded volume (contracts) with Bullish / Neutral / Bearish label.
- **India VIX:** Closing value.

All PCR and VIX fields show an amber **Unavailable** badge when the backend job ran but could not obtain the value (e.g., holiday, data not yet published, or NSE API errors).

**PCR interpretation:**

| Value    | Signal   |
|----------|----------|
| ≥ 1.3    | Bullish (high put buying = hedging, market likely to rise) |
| 0.8–1.3  | Neutral  |
| < 0.8    | Bearish (low put buying = complacency) |

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
