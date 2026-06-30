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
  Salaries are anchored to the candidate's **local market in EUR** (not inflated US/remote figures), and the
  plan can be **exported to PDF**.
- **Adversarial self-critique** of the career plan — after generating, an independent reviewer (framed as
  critiquing a rival tool, so it's honest) red-teams the plan for inflated salaries, over-optimism and
  contradictions, so the user calibrates trust. Three user-selectable depths: **Critique** (flag weak points),
  **Debate** (the author rebuts), **Debate + revise** (re-searches for real local data and rewrites the plan).
- **Reliable key-free web search** — search runs through **Jina Reader** (renders the SERP server-side, so it
  isn't blocked like a raw scrape), with the keyless Mojeek/DuckDuckGo scrape kept as a fallback; pages can be
  fetched and read in full to ground figures the snippets omit. Still no key, no setup.
- **Local-model manager** — pick/manage the local model from the UI: detect the running OpenAI-compatible
  runtime, list installed models, select the active one, and an in-app Ollama installer (streamed `/api/pull`).
- **Live model browser** — search **Ollama** (scrapes ollama.com) and **LM Studio**'s catalog (the
  `lmstudio-community` Hugging Face org) live, with capability badges, sizes/quants and one-click install
  (Ollama pull · direct GGUF download into `~/.lmstudio/models`) with per-row progress. The installed list
  detects **both** runtimes (Ollama via `/api/tags`, LM Studio by scanning its models folder), and clear
  guidance covers using LM Studio (start its server + load the model). Keyless, no `lms` CLI dependency.
- **JSearch job source** — optional free Google-for-Jobs aggregator via **OpenWeb Ninja** (direct) or
  **RapidAPI**, with a provider picker and per-country results.
- **Delete jobs** — clear the saved job cache from the UI (with confirmation), and a settings unsaved-changes
  guard so edits aren't lost on navigation.
- **Rebranding** — settled product identity: the **Job Radar** name, the concentric-ring radar mark used as
  logo + app icon, and the "mission control" palette.
- **Company Researcher** — a dedicated **Companies** view that aggregates employer-health signals across the
  matched jobs (and any company you type in): **satisfaction** (Glassdoor/kununu ★, review count, recommend %,
  CEO approval, sub-ratings, eNPS, interview difficulty), **recent layoffs** (date + scale + source), **recent
  signals** (funding/acquisition/hiring-freeze/leadership), **typical pay** (local-market band), **tenure**, and
  **firmographics** grounded by **Wikidata** (founded, HQ, industry, CEO, size) plus the **tech stack** seen in
  the employer's own postings. Every field is **source-linked, confidence-tagged, and "unknown" when thin** (no
  fabricated numbers). Per-company cache with an "as of" date + TTL, name filter, sortable, and **export** to
  CSV/HTML/PDF. Key-free (search snippets + a Jina page-fetch + Wikidata + your own AI engine).

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

The career-plan area shipped (see above), now with adversarial self-critique. Next for it: **track progress
over time** — save successive plans and surface what changed as the profile and market move, so the plan
becomes a living document.

### 3. Pause / resume classification

Let the user **pause an in-progress run** — scoring jobs or researching companies — and resume it later,
without losing what's already done. Useful for long scans or to stop spending tokens mid-run. Already-scored
jobs are persisted, so resuming should pick up where it left off; the work is a cancellation/pause control in
the UI wired through the streaming pipeline (and the per-company research).

### 4. Company Researcher — deepen it

The **Company Researcher** shipped (see above). Remaining ideas: **company chips on the job card** (★3.9 · ⚠
layoffs 2025 · ~€48k) that jump to the company; an optional **"company-health" factor** fed into scoring /
red-flags (transparently); **trend over time** (re-research and surface what changed); **Portugal registry**
enrichment (nif.pt / Racius — legal name, CAE sector, share capital) for local SMEs that have no Glassdoor /
Wikidata presence; and an optional **BYOK slot** for an official reviews/firmographics API (Coresignal / People
Data Labs / Crunchbase) for users who want hard data instead of best-effort snippets.

---

## 💡 Backlog / ideas

- **Premium UI polish** — elevate the front-end to feel like a premium, polished product: refined visual
  design, smooth transitions/motion, consistent spacing & iconography, and proper empty/loading/error states.
- **LinkedIn aggregation** — LinkedIn isn't scraped (ToS). Today: a "Procurar no LinkedIn" button opens
  LinkedIn Jobs in the browser pre-filled from the profile, plus the optional `linkedin-jobs.json` merge.
  Explore a ToS-respecting way to pull results into the app (e.g. a user-run browser snippet/export, or a
  Playwright-assisted pass where the **user logs in** — best-effort, opt-in, given LinkedIn's bot defenses).
- **Deeper company research** — *(promoted to "Company Researcher", Planned #4)*. Remaining sub-idea: optional
  use of the **Claude CLI's native web search** to ground the extracted signals more richly.
- More job sources (Careerjet, Jooble) behind the existing pluggable `Source` interface.
- A non-tech sample dataset to showcase the field-agnostic scoring.
- Saved searches and change alerts (new strong-fit jobs since last run).
- **One-click installer for all platforms** — *(in place — needs a first tagged run to validate)*. A
  `release` GitHub Actions workflow builds self-contained packages on each OS runner: Inno Setup `.exe`
  (Windows), `.dmg` for Apple Silicon + Intel (macOS) and an `.AppImage` (Linux), and attaches them to the
  GitHub Release on a `v*` tag. App state moved to a per-user data dir so installed (read-only) builds work.
  Still unsigned (SmartScreen/Gatekeeper warnings); code signing + auto-update are the next polish.
