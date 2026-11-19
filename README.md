# MARK_UP Sales App — Setup, Run and User Manual

A field sales, order and billing app for a small distribution business.

- **Salespeople** mark attendance automatically, visit parties on their beat, take orders (works offline), and share each order to WhatsApp with its location.
- **Billing staff** mark each order **Bill done** or **Cancel** (with a reason). Only billed orders count as sales.
- **Admin** manages all master data and targets and sees every report.
- **Reports**: Party Name List, Daily DSR, Product Report, Target vs Achievement (Daily / MTD / Monthly / Overall), Product Target vs Achievement — each downloadable as **Excel** or **PDF**.
- **Data** is stored live in a **Google Sheet** owned by the client.

---

## 1. What is in this folder

```
MarkUp/
├── MarkUp.sln                 Solution (open in Visual Studio / Rider / VS Code)
├── Dockerfile                 Builds the API container (used by Render)
├── MarkUp.Shared/             Models, request/response classes and pricing rules used by both apps
├── MarkUp.Api/                ASP.NET Core 9 Web API (Google Sheets, login, business rules, reports, export)
└── MarkUp.Web/                Blazor WebAssembly PWA (the app people use on phone and desktop)
    └── wwwroot/               Static files, PWA manifest, service worker, vercel.json
```

| Part | Technology | Where it runs |
|---|---|---|
| App (all roles) | Blazor WebAssembly PWA (.NET 9) | Vercel (static hosting) |
| API | ASP.NET Core 9 Web API | Render (Docker) |
| Data | Google Sheets (service account) | Client's Google account |
| Excel / PDF | ClosedXML / QuestPDF (Community licence) | Inside the API |
| Place names | OpenStreetMap Nominatim (reverse geocoding) | Called by the API |

---

## 2. Prerequisites

1. **.NET 9 SDK** — https://dotnet.microsoft.com/download (check with `dotnet --version`, must be 9.x or newer).
2. A **Google account** (the client's, ideally) for the spreadsheet.
3. For deployment: free accounts on **GitHub**, **Render** (https://render.com) and **Vercel** (https://vercel.com), plus **Node.js** for the Vercel CLI.

---

## 3. Google Sheets setup (one time)

### 3.1 Create a service account
1. Open https://console.cloud.google.com and create a project, e.g. `markup-sales`.
2. **APIs & Services → Library** → search **Google Sheets API** → **Enable**.
3. **APIs & Services → Credentials → Create credentials → Service account**. Name it `markup-api`, click through and **Done** (no roles needed).
4. Open the service account → **Keys → Add key → Create new key → JSON**. A `.json` file downloads.
   - Save it **outside** the project folder, e.g. `C:\secrets\markup-sa.json`.
   - **Never commit it to Git or share it.**
5. Copy the service account **email** (looks like `markup-api@markup-sales.iam.gserviceaccount.com`).

### 3.2 Create the spreadsheet
1. In Google Sheets create a blank spreadsheet named **MARK_UP Data** (in the client's account).
2. Click **Share** → paste the service account email → role **Editor** → Send.
3. Copy the **spreadsheet ID** from the URL: `https://docs.google.com/spreadsheets/d/`**`THIS_PART`**`/edit`

Leave it empty. When the API starts it creates all tabs and header rows automatically:
`Users, PartyTypes, Holidays, CalendarSettings, Dealers, Beats, Parties, Products, Schemes, Targets, ProductTargets, Attendance, Visits, Orders, OrderItems`.

Each tab gets a frozen header and a *warning-only* protection, so anyone editing by hand sees a warning. **Enter and change data through the app, not in the sheet.** You can safely add your own extra tabs (e.g. pivot tables) for the client.

---

## 4. Run the API locally

Open a terminal in `MarkUp/MarkUp.Api` and set secrets (stored outside the project by .NET user-secrets):

```powershell
dotnet user-secrets set "GoogleSheets:SpreadsheetId" "<your spreadsheet id>"
dotnet user-secrets set "GoogleSheets:CredentialsPath" "C:\secrets\markup-sa.json"
dotnet user-secrets set "Jwt:Key" "<random text, at least 32 characters>"
dotnet user-secrets set "Seed:AdminUsername" "admin"
dotnet user-secrets set "Seed:AdminPassword" "ChangeMe123"
```

Tip — generate a strong JWT key in PowerShell:
```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
```

Run it:
```powershell
dotnet run --launch-profile http
```

The API listens on **http://localhost:5100**. Check **http://localhost:5100/health** → `{"status":"ok"}`.
On first start it builds the sheet structure and creates the first **Admin** from the `Seed` settings (only when the Users tab is empty). That admin must change the password at first login.

---

## 5. Run the app locally

In a second terminal, in `MarkUp/MarkUp.Web`:

```powershell
dotnet run --launch-profile http
```

Open **http://localhost:5200** and log in with the seed admin.

- The app reads the API address from `MarkUp.Web/wwwroot/appsettings.json` (`http://localhost:5100/`).
- The API only accepts calls from origins listed in `App:AllowedOrigins`. `appsettings.Development.json` already allows `http://localhost:5200` and `https://localhost:7200`.
- Location (GPS) in browsers only works on `https://` or `localhost`. To test on a phone, deploy (section 8) or use a tunnel with HTTPS.

---

## 6. First-time setup in the app (Admin)

Do these in order (each step needs the one before). Everything is under the **Admin** menu.

1. **Settings**
   - **Party types** — e.g. Retailer, Wholesaler, Modern Trade.
   - **Weekly off days** — one row per year, e.g. `Saturday` (or `Saturday,Sunday`). If a year has no row, Saturday is used.
   - **Holidays** — public holidays (Dashain, Tihar...). Used to count working days for targets.
2. **Dealers** — distributors.
3. **Users** — create **Sales** users (each linked to one dealer) and **Bill** users. Every new user must change their password at first login.
4. **Beats** — each beat belongs to a dealer and is **fixed to one salesperson** of that dealer. A salesperson may have several beats.
5. **Parties** — shops, each on one beat with a party type. Only the Admin creates parties.
6. **Products** — code, group, name, base unit (e.g. Pcs), optional 2nd unit (e.g. Box) with conversion (e.g. 12), MRP NPR/INR, dealer rate, retailer rate.
7. **Schemes** — per product: type **Percent**, **Flat** or **Free quantity**, value, minimum quantity, valid dates.
8. **Targets** — monthly **value** target per salesperson, and optional monthly **product quantity** targets.

Deactivate instead of delete: deactivated records disappear from lists but stay in history and reports.

---

## 7. Using the app

### 7.1 Install on a phone
Open the app link in **Chrome (Android)** or **Safari (iPhone)**:
- Android: menu **⋮ → Add to Home screen / Install app**.
- iPhone: **Share → Add to Home Screen**.

Allow **Location** when asked. The phone's date, time and time zone must be correct (Nepal time).

### 7.2 Salesperson (Sales role)
1. **Log in.** Attendance is captured automatically (date, day, time, GPS location). If location is blocked, allow it in phone settings and tap **Mark attendance**. Visits cannot be saved until attendance is marked.
2. The **Market Visit** form opens. Date, day and beat fill in automatically (if several beats are assigned, choose one).
3. For each shop:
   - Choose the **party**.
   - **Order:** add products (search by name/code), enter quantity in Unit or 2nd Unit, optionally pick a **scheme**. The app shows rate, discount, free quantity and the total.
   - **No order:** leave products empty and type the **reason** (required).
   - Tap **Save order / Save visit**. The location is captured.
4. After an order is sent, tap **Share on WhatsApp** → choose the group → **Send**. The message contains the order and a Google Maps link to where it was taken.
5. **No network?** Everything is saved on the phone and sent automatically when the connection returns (or tap **Send now**). Records the server refuses are shown with the reason — fix and **Try again**, or **Remove**.
6. **Reports** shows your own DSR and targets.

### 7.3 Billing staff (Bill role)
- **Today's Orders**: every order placed today.
  - **✓ Bill done** — the order now counts as a sale.
  - **Cancel order** — a reason is required; it appears in report Remarks.
- **Old Pending Orders**: earlier orders not yet billed, including cancelled ones. Tick **Bill done** when the bill is made later.
- Billed orders cannot be cancelled. Only **Bill done** orders count in any sales figure.

### 7.4 Admin
Everything above plus master data, users, targets and all reports for all users.

### 7.5 Reports
Choose the report, date, view and filters → **Show report**, or **Download Excel / PDF**.

| Report | Filters | Notes |
|---|---|---|
| Daily DSR | Date, salesperson | One row per order line; Productivity Yes/No; scheme and no-order reason shown |
| Target vs Achievement | Date, view, salesperson | Achievement = billed order value. Remarks = cancel reason, or "Not billed yet" |
| Product Target vs Achievement | Date, view, salesperson | Quantities in base units; only billed orders count |
| Party Name List | Salesperson, dealer, beat | Active parties |
| Product Report | — | Active products |

Sales users always see only their own data. Admin and Bill users see everyone.

---

## 8. Business rules (how numbers are calculated)

- **Pricing:** line value = base quantity × **retailer (RT) rate**. 2nd unit quantities are converted to base units first.
  - Percent scheme: discount = gross × value ÷ 100.
  - Flat scheme: discount = value (NPR), never more than the line.
  - Free quantity scheme: value base units are free; line value unchanged.
  - A scheme applies only between its dates and when the line reaches its minimum quantity.
  - The server always recalculates prices; totals from a phone are never trusted.
- **Working days** = days in month − weekly off days − holidays.
- **Daily target** = monthly target ÷ working days in the month (0 on a non-working day).
- **MTD target** = daily target × working days from the 1st to the report date.
- **Achievement** = sum of **billed** orders, by the date the order was taken.
- **Billed value** always equals the order value (no partial billing).
- **Order numbers**: `ORD-yyyyMMdd-0001`, per day.
- Offline records can be synced up to 7 days late (`App:MaxOfflineDays`). A phone clock more than 10 minutes ahead is rejected.

---

## 9. Deploy (free tiers)

### 9.1 Put the code on GitHub
Create a **private** repository and push this folder. `.gitignore` already excludes build output and key files.

### 9.2 API on Render (Docker)
1. Render → **New → Web Service** → connect the repo.
2. **Runtime: Docker**, Dockerfile path `./Dockerfile`, root directory = repo root. Instance type **Free**. Region: **Singapore** (closest to Nepal).
3. **Environment variables** (Render uses `__` for nested settings):

| Key | Value |
|---|---|
| `GoogleSheets__SpreadsheetId` | your spreadsheet id |
| `GoogleSheets__CredentialsBase64` | the key file as base64 (see below) |
| `Jwt__Key` | random text, 32+ characters (different from local) |
| `Seed__AdminUsername` | `admin` (only used when the Users tab is empty) |
| `Seed__AdminPassword` | a strong temporary password |
| `App__AllowedOrigins__0` | `https://<your-app>.vercel.app` |
| `App__GeocodingUserAgent` | `MarkUpSalesApp/1.0 (your-email@example.com)` |

   Base64 of the key file:
   - PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\secrets\markup-sa.json"))`
   - macOS/Linux: `base64 -w0 markup-sa.json`

4. **Health check path:** `/health`. Deploy. Your API URL looks like `https://markup-api.onrender.com`.
5. **Keep it awake** (free instances sleep after ~15 minutes idle): create a free monitor on https://uptimerobot.com or https://cron-job.org that calls `https://<api>/health` every 10 minutes during working hours.

Run **one instance only** (the free plan does). The API serialises writes to the sheet in memory; several instances could interleave writes.

### 9.3 App on Vercel
1. Set the real API address in `MarkUp.Web/wwwroot/appsettings.Production.json`:
   ```json
   { "ApiBaseUrl": "https://markup-api.onrender.com/" }
   ```
2. In `MarkUp.Web/wwwroot/vercel.json`, replace `https://YOUR-API-NAME.onrender.com` in the `Content-Security-Policy` `connect-src` with the same API address.
3. Publish and deploy:
   ```powershell
   cd MarkUp.Web
   dotnet publish -c Release -o ..\publish\web
   npm install -g vercel
   cd ..\publish\web\wwwroot
   vercel deploy --prod
   ```
   The first time, Vercel asks to link/create a project. Accept the defaults (no build command; the folder is already built).
4. Add the final Vercel URL (e.g. `https://markup-sales.vercel.app`) to `App__AllowedOrigins__0` on Render if it differs, and redeploy the API.

To update the app later: change code → repeat step 3. Phones pick up the new version the next time the app is opened.

---

## 10. Security summary

- Passwords hashed with **BCrypt**; minimum 8 characters with a letter and a number; forced change at first login and after an admin reset.
- **JWT** tokens (12 h). Every request re-checks that the user still exists, is active and has the same role and password — deactivating a user or changing a password signs them out everywhere.
- **Login protection:** 10 attempts/minute per IP, and the account locks for 15 minutes after 5 wrong passwords.
- **Role checks on the server** for every endpoint; Sales users can only touch their own beats, parties and data.
- **Validation** of every input on the server; prices recalculated on the server.
- **No formula injection:** values are written to Google Sheets as raw text and to Excel as text/numbers, never formulas.
- **CORS** limited to your app's origin; strict **Content-Security-Policy** and security headers on both app and API; 1 MB request limit; no stack traces sent to clients.
- **Secrets** live in user-secrets / Render environment variables, never in code.
- The login token is kept in the browser's storage so the app works offline; the strict CSP protects it against injected scripts. Use a phone lock screen.

---

## 11. Backups

- Google Sheets keeps **version history** (File → Version history).
- Make a weekly copy: **File → Make a copy** (or a Google Apps Script time trigger that copies the file).

---

## 12. Limitations

| Limitation | Effect / workaround |
|---|---|
| WhatsApp group posting needs one tap | The message is pre-filled; the salesperson picks the group and taps Send (WhatsApp does not allow automatic posting to groups). |
| Free API sleeps when idle | First request may take 30–60 s. Use the uptime pinger or Render's paid plan (~USD 7/month). |
| Google Sheets is not a full database | Fine for ~10–25 users. API quota ~60 requests/min per user, 10 million cells per file. The data layer (`SheetStore`) can be replaced by a database later. |
| Offline records live only on the phone until sent | If the phone is lost or browser data cleared before sync, they are lost. Keep the app opening online regularly. |
| GPS accuracy / spoofing | Indoor GPS can be off; fake-GPS apps exist on Android. Coordinates are stored for review. |
| iPhone PWA | Works through Safari; iOS may clear app data if unused for weeks. |
| Nepali (Devanagari) text in PDFs | Displays in the app and Excel; PDF needs a Devanagari font installed in the container (add `fonts-noto-core` to the Dockerfile if needed). |

---

## 13. Troubleshooting

| Problem | Fix |
|---|---|
| API stops at start with "Jwt:Key must be set" | Set `Jwt:Key` (32+ chars). |
| "Google credentials file not found" / 403 from Google | Check the path or base64 value, that the Sheets API is enabled, and that the sheet is shared with the service account as Editor. |
| App shows "Cannot reach the server" | API asleep (wait a minute), wrong `ApiBaseUrl`, or the app's origin is missing from `App:AllowedOrigins` (browser console shows a CORS error). |
| Calls blocked in the browser console with "Content Security Policy" | Update `connect-src` in `vercel.json` to the exact API URL and redeploy. |
| "The phone's date/time is ahead" | Set the phone to automatic date/time. |
| Location never works | Must be HTTPS (or localhost) and location permission allowed for the browser and the site. |
| Nobody can log in on a new sheet | Set `Seed:AdminUsername` and `Seed:AdminPassword` and restart the API (only works while the Users tab is empty). |
| Forgot admin password | Another admin resets it under Users. If there is no other admin: in the sheet, clear the Users tab rows (keep the header), set the Seed values and restart — this creates a fresh admin. |

---

## 14. Developer notes

- NuGet versions use floating ranges (`9.*`, `1.*`, ...) so the latest compatible packages are restored. After the first successful build, pin the exact versions you got (see `obj/project.assets.json`) for repeatable builds.
- Add a column: add a property to the model in `MarkUp.Shared/Models.cs` (or `ServerModels.cs`). On next start the API appends the new header to the tab.
- API endpoints (all JSON, bearer token):
  - `POST /api/auth/login`, `POST /api/auth/change-password`, `GET /api/auth/me`
  - `GET|POST|PUT|DELETE /api/masters/{party-types|holidays|calendar-settings|dealers|beats|parties|products|schemes|targets|product-targets}` (Admin)
  - `GET|POST|PUT|DELETE /api/users` (Admin)
  - `GET /api/lookups`
  - `GET /api/sales/bootstrap`, `POST /api/sales/attendance`, `POST /api/sales/visits` (Sales)
  - `GET /api/billing/today`, `GET /api/billing/pending`, `POST /api/billing/{id}/bill`, `POST /api/billing/{id}/cancel` (Admin, Bill)
  - `GET /api/reports/{party-list|dsr|products|target-vs-ach|product-target}?date=&userId=&dealerId=&beatId=&view=&format=json|xlsx|pdf`
  - `GET /health`
