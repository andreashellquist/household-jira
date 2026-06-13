# Hemma — household board

A Jira-inspired but radically simplified app for running a household. Mobile-first web app served by a .NET backend, made to be pinned to the home screen and used by the whole family.

## Features

- **Task board (Tavla)** — kanban with *Att göra → Pågår → Klart*. Tasks have a color-coded category (kök, städ, tvätt, trädgård, fix, barn, husdjur, ärenden), priority, assignee, due date, and notes. One tap on ✓ advances a card; tap the card to edit.
- **Recurring chores** — set *upprepa* (daily/weekly/monthly…) and completing the chore automatically creates the next occurrence.
- **Shopping list (Inköp)** — add with quantity, tap to check off, clear all checked. Open-item count badges the nav.
- **Meal planner (Mat)** — type dinner plans straight into the week view.
- **Family (Familj)** — members with color avatars, assignment, and a "done this week" counter per person.

Done tasks older than a week drop off the board automatically.

## Stack

- ASP.NET Core minimal API (.NET 10) + EF Core with SQLite (`household.db`, created on first run)
- Vanilla JS/CSS single-page frontend in `wwwroot` — no build step
- Installable as a PWA (manifest + icon)

## Run

```bash
dotnet run --project src/Household.Api
```

Open http://localhost:5240. To use it from phones, run it on a machine on your home network and browse to `http://<host>:5240` (bind with `--urls http://0.0.0.0:5240`).

## API

All data flows through `/api/*`: one `GET /api/bootstrap` round-trip loads the app; CRUD endpoints for chores, shopping items, meals (upsert by date+slot), and members. See [Program.cs](src/Household.Api/Program.cs).
