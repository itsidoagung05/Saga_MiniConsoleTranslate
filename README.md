# Saga_MiniConsoleTranslate

Mini console untuk otomasi caching translasi ke SQLite `LocalStorage` dengan alur:
- Launch/attach `Saga.MainApplication`
- Login (admin + optional employee account)
- Crawl halaman aman (GET/view only)
- Ekstrak candidate text dari Razor view + source code message (TempData/Exception/ModelState/Result.Failure)
- Backfill translasi ke kolom bahasa (`Indonesia`, `Korea`, `Arabic`, `Chinese`)
- Tulis report JSON/TXT

## Prasyarat
- .NET 8 SDK
- Browser Playwright terinstall

Install browser Playwright (sekali saja):
```powershell
cd Saga_MiniConsoleTranslate
dotnet build
bin\Debug\net8.0\playwright.ps1 install
```

## Konfigurasi
File utama: `appsettings.json`

Section penting:
- `MainApplicationRunner`
- `AutomationAccount`
- `Selenium`
- `TranslationAutomation`
- `TranslationProviders:IflytekTranslate`
- `TranslationProviders:LaraTranslate`

Catatan:
- Gunakan environment variable / user-secrets untuk credential API.
- Untuk direct update DB web utama, pakai `SqliteMode: "SharedFile"` dan arahkan `SourceSqlitePath` ke `Saga.MainApplication/LocalStorage.db`.

## Menjalankan
```powershell
dotnet run --project Saga_MiniConsoleTranslate/Saga_MiniConsoleTranslate.csproj
```

## Output Report
Default folder: `bin/Debug/net8.0/reports`

File output:
- `summary-<timestamp>.json`
- `detail-<timestamp>.json`
- `residual-<timestamp>.json`
- `report-<timestamp>.txt`
- `deleted-noise-<timestamp>.json`

## Catatan Keamanan Crawl
- Tidak melakukan submit aksi bisnis destruktif (`delete/save/approve/reject/process/confirm/logout`).
- Hanya GET/open/view yang aman.

