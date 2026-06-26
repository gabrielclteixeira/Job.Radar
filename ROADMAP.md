# Roadmap

Where Job Radar is heading. Everything stays **local-first and BYOK** — features that need an LLM use
your own Claude CLI, and a token-free path (demo/cached) is kept wherever it makes sense.

## ✅ Shipped

- CV (PDF) → AI-built, editable profile (PdfPig + Claude CLI).
- Job aggregation from public APIs/ATS, profile-aware filtering, AI **or** keyword scoring (toggle).
- Per-job transparency: clickable listing + expandable "why this score" breakdown.
- Live **streaming** of results as they're classified; already-scored jobs are remembered (no re-spend).
- Saved profile reused across runs; "use a sample CV" (John Doe) to try it instantly with no tokens.
- Export to CSV / HTML / PDF.
- **Pluggable LLM backend** — Claude CLI or any OpenAI-compatible local model (Ollama, LM Studio,
  llama.cpp) via a `provider` setting; runs fully offline with no Claude subscription.
- **Generate a CV PDF** from the profile (CV Studio, first step) — one-page styled PDF via the HTML→PDF path.
- **Company research** — per-job button that web-searches employer reviews + comparable salaries (key-free,
  DuckDuckGo) and summarises them with your model. Works with local models too — they summarise the fetched
  snippets (the search step provides the web data the offline model can't).
- **Improvement (Evoluir)** — a personalised career-growth plan from your profile + the jobs the radar has
  already scored, powered by **multi-step deep research** (seed searches → the model picks angles to dig into →
  targeted searches → synthesis). Returns strengths, skill gaps with actions, target roles, a **salary
  trajectory** (now → 12–24 months) and time-boxed next steps — with sources. Key-free search, BYOK.
- **Rebranding** — settled product identity: the **Job Radar** name, the concentric-ring radar mark used as
  logo + app icon, and the "mission control" palette.

---

## 🔭 Planned

### 1. CV Studio — build & refine your CV

A dedicated area to **create and improve a CV**, not just consume one.

- **Import experience from elsewhere**
  - Pull roles, dates and highlights from **LinkedIn** (ToS-safe: a profile export / manual paste, no scraping)
    and **GitHub** (repos, languages, notable contributions) to seed or enrich the CV.
  - Merge with the existing CV-derived profile so nothing is re-typed.
- **Generate a fresh PDF** ✅ *(basic version shipped)*
  - Richer output once work history is imported (below).
- **Front-end customization & refinement**
  - Pick a **template/theme** and tune the look (layout, accent colour, density, fonts).
  - **Rich content control** so the user shapes each section: **highlight/emphasize text**, insert a
    **table** (e.g. skills matrix, project list), reorder/show/hide sections, add callouts.
  - **AI-assisted refinement**: rewrite a bullet, tighten the summary, tailor the CV to a specific job —
    with the user always in control of the final text.

### 2. Improvement — deepen it

The career-plan area shipped (see above). Next for it: **track progress over time** — save successive plans
and surface what changed as the profile and market move, so the plan becomes a living document.

---

## 💡 Backlog / ideas

- **Premium UI polish** — elevate the front-end to feel like a premium, polished product: refined visual
  design, smooth transitions/motion, consistent spacing & iconography, and proper empty/loading/error states.
- **Local-model manager** — a UI section to pick and manage the local model, runtime-agnostic. The app
  already talks to any OpenAI-compatible runtime (LM Studio, Ollama, llama.cpp) via the `provider`/`baseUrl`
  setting; this adds: detect the running runtime, list installed models, and select the active one. Include
  an **optional in-app installer for Ollama** (its `/api/pull` API streams download progress); LM Studio /
  llama.cpp users manage models in their own tools and just point the app at their endpoint.
- **LinkedIn aggregation** — LinkedIn isn't scraped (ToS). Today: a "Procurar no LinkedIn" button opens
  LinkedIn Jobs in the browser pre-filled from the profile, plus the optional `linkedin-jobs.json` merge.
  Explore a ToS-respecting way to pull results into the app (e.g. a user-run browser snippet/export, or a
  Playwright-assisted pass where the **user logs in** — best-effort, opt-in, given LinkedIn's bot defenses).
- **Deeper company research** — build on the shipped company-research step: more/structured sources,
  optional use of the Claude CLI's native web search, and caching results per company.
- More job sources (Careerjet, Jooble) behind the existing pluggable `Source` interface.
- A non-tech sample dataset to showcase the field-agnostic scoring.
- Saved searches and change alerts (new strong-fit jobs since last run).
- **One-click installer for all platforms** — *(in place — needs a first tagged run to validate)*. A
  `release` GitHub Actions workflow builds self-contained packages on each OS runner: Inno Setup `.exe`
  (Windows), `.dmg` for Apple Silicon + Intel (macOS) and an `.AppImage` (Linux), and attaches them to the
  GitHub Release on a `v*` tag. App state moved to a per-user data dir so installed (read-only) builds work.
  Still unsigned (SmartScreen/Gatekeeper warnings); code signing + auto-update are the next polish.
