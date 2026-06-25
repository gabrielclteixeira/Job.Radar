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

---

## 🔭 Planned

### 1. CV Studio — build & refine your CV

A dedicated area to **create and improve a CV**, not just consume one.

- **Import experience from elsewhere**
  - Pull roles, dates and highlights from **LinkedIn** (ToS-safe: a profile export / manual paste, no scraping)
    and **GitHub** (repos, languages, notable contributions) to seed or enrich the CV.
  - Merge with the existing CV-derived profile so nothing is re-typed.
- **Generate a fresh PDF**
  - Produce a new, polished CV PDF from the structured profile (reuse the HTML→PDF pipeline).
- **Front-end customization & refinement**
  - Pick a **template/theme** and tune the look (layout, accent colour, density, fonts).
  - **Rich content control** so the user shapes each section: **highlight/emphasize text**, insert a
    **table** (e.g. skills matrix, project list), reorder/show/hide sections, add callouts.
  - **AI-assisted refinement**: rewrite a bullet, tighten the summary, tailor the CV to a specific job —
    with the user always in control of the final text.

### 2. Improvement — career growth & planning

A space focused on *getting better positioned*, not just finding listings.

- **Generate a career plan** from the profile + the job market the app already sees
  (skill gaps, target roles, salary trajectory, suggested next steps).
- **Deep research** powering the plan: multi-step web research across companies, salaries, in-demand
  skills and hiring trends — synthesized into the plan, with sources. *(This area requires deep search.)*
- Track progress over time (revisit and update the plan as the profile and market change).

### 3. Local models — bring your own LLM

Today the AI features shell out to the **Claude CLI**. Next, make the LLM backend **pluggable** so users
can run everything against a **local model** instead.

- Support local runtimes (**Ollama**, **LM Studio**, **llama.cpp** / any OpenAI-compatible endpoint).
- A **provider setting** to pick the backend (Claude CLI · local · OpenAI-compatible URL) and the model name.
- Fully **offline & private**: CV parsing, scoring and (later) CV refinement run on the user's own hardware,
  no CLI and no cloud. Keeps the local-first/BYOK promise even without a Claude subscription.

---

## 💡 Backlog / ideas

- **Rebranding** — pick a real product name and identity (logo, colours, icon) to replace the working
  title "Job Radar".
- **Premium UI polish** — elevate the front-end to feel like a premium, polished product: refined visual
  design, smooth transitions/motion, consistent spacing & iconography, and proper empty/loading/error states.
- More job sources (Careerjet, Jooble) behind the existing pluggable `Source` interface.
- A non-tech sample dataset to showcase the field-agnostic scoring.
- Saved searches and change alerts (new strong-fit jobs since last run).
