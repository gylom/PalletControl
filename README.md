# Pallet Control v5.4

Full .NET + React/Vite version.

## New in v4

### Health Check (all logged-in users)
- Always-visible compact Health Check.
- API status.
- SQLite database status using a real database read.
- Overall healthy/unhealthy state.
- Automatic refresh every 15 seconds + manual refresh button.

### Registration safety
- Vehicle and driver always reset to **Choose vehicle / Choose driver** after a successful submission.
- Driver dropdown is ranked by previous use on the selected vehicle, but never auto-selects a driver.
- Configurable warnings before submission:
  - Large IN quantity.
  - Large OUT quantity.
  - Same vehicle submitted recently.
  - Same driver submitted recently.
  - Exact possible duplicate.
  - Too many rapid submissions from a vehicle.
  - High same-vehicle daily total.
- Warnings require user confirmation before submission.

### Warning Center
- Visible to **Admin + Superuser**.
- Admin and Superuser can acknowledge warnings.
- **Only Admin can configure warning rules or thresholds.**
- Cancellation and cancellation-reversal events can also appear as warnings.

### Receipts
- Receipts tab is available to all roles.
- User: latest 25 only for selected date; no 50/All buttons.
- Admin/Superuser: default 25, buttons for 25 / 50 / All on selected date.
- Newest/oldest timestamp sorting.
- Cancelled receipt badge and information button.
- Cancellation history shows who, timestamp and reason.
- Admin/Superuser can reverse a cancellation.
- Reversals remain in the audit history.

### Admin master data
- Add/delete Transporters.
- Add/delete Driver names.
- Add/delete Vehicles.
- Change a vehicle's Transporter.
- Receipt snapshots preserve historical Vehicle / Driver / Transporter text even after master data is deleted.
- Manage pallet types.
- Create users, edit display name/role/terminal/active state, reset passwords.

### Statistics
- Existing date, pallet-type, transporter, vehicle and driver filters.
- Highest-to-lowest sorting options.
- **Best Performing Driver** panel:
  - This week.
  - This month.
  - Last month.
  - Uses current pallet-type filter when selected.
  - Gold/silver/bronze top three.

### Submit notifications
Admin can globally enable/disable:
- Monthly milestone messages (default every 100 pallets IN).
- Current monthly balance message.
- Monthly leaderboard messages.

Each user can also disable their own non-critical milestone / leaderboard / balance messages in **Settings**.

## Fresh database

v4 uses:

`palletcontrol-v4.db`

This intentionally creates a fresh schema and does not touch the previous dummy `palletcontrol.db`.

## Demo accounts

- Admin: `admin` / `admin123`
- Superuser: `super` / `super123`
- User: `user` / `user123`

Change demo passwords and the JWT key before production use.

## Start backend

```powershell
cd backend\PalletControl.Api
dotnet restore
dotnet run
```

The API listens on:

`http://localhost:5000`

## Start frontend

Open a second terminal:

```powershell
cd frontend
npm install
npm run dev
```

Vite starts on port 5173, or the next free port (for example 5174). The `/api` proxy points to `http://localhost:5000`.

## Optional smoke test

With the backend running:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\smoke-test.ps1
```

`-Scope Process` only changes PowerShell policy for that terminal window. Closing the terminal restores the previous policy automatically.


## v5.1 receipt UI update

- Receipt pallet quantities are shown in the same information row as Transporter, Vehicle, Driver and Direction.
- Regular Users see the latest 25 receipts across dates with no date, sort or limit controls.
- Admin/Superuser keep Date, sort and 25/50/All controls.
- The backend enforces the regular-user 25-receipt limit.


## v5.4 additions
- Admin/Superuser receipt filters: All, Active, Cancelled, Reversed.
- Reversed receipts retain a visible history badge and receipt audit history.
- Warnings page search across receipt number, vehicle, driver, transporter, warning type/message and users.
- Admin/Superuser can choose the business date when registering a receipt. Normal users remain locked to today.
- Manual/backdated receipt dates are audit logged with receipt number, chosen business date, actual UTC submission time, vehicle, driver, transporter and submitting user.


## v5.4
- Added receipt search for Admin and Superuser only.
- Search works together with date, status, sort and 25/50/All controls.
- Search is performed before the result limit and can match receipt number, vehicle, driver, transporter, direction, status, cancellation reason, pallet type/quantity, and receipt action users/reasons.
- Regular Users remain restricted to their latest 25 receipts and cannot use receipt search.


## v5.4 Admin category update

- Admin opens as a category menu instead of one long settings page.
- Categories: Users, Vehicles, Driver names, Transporters, Pallet types, Warning rules, Notifications & general.
- No master-data/settings payload is loaded when the Admin page first opens.
- Only the selected category is requested from the backend and rendered.
- Warning configuration and notification/general settings are separated into their own pages.
