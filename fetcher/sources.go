package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Job is the normalized shape emitted to stdout and consumed by the C# core.
type Job struct {
	Title          string  `json:"title"`
	Company        string  `json:"company"`
	Location       string  `json:"location"`
	Remote         string  `json:"remote"` // remote | hybrid | onsite | ""
	URL            string  `json:"url"`
	Description    string  `json:"description"`
	Source         string  `json:"source"`
	PostedAt       string  `json:"postedAt"`
	SalaryMin      float64 `json:"salaryMin"`      // 0 if unknown
	SalaryMax      float64 `json:"salaryMax"`      // 0 if unknown
	SalaryCurrency string  `json:"salaryCurrency"` // "EUR", "USD", ... ("" if unknown)
}

// Config drives which sources run. Loaded from a JSON file (-config).
type Config struct {
	Queries    []string `json:"queries"`
	Location   string   `json:"location"`
	Adzuna     struct {
		AppID   string `json:"appId"`
		AppKey  string `json:"appKey"`
		Country string `json:"country"`
	} `json:"adzuna"`
	Greenhouse []string `json:"greenhouse"` // board tokens
	Lever      []string `json:"lever"`      // company slugs
	Remotive   bool     `json:"remotive"`
	RemoteOK   bool     `json:"remoteok"`
	Arbeitnow  bool     `json:"arbeitnow"`
}

// Source fetches jobs from one provider.
type Source interface {
	Name() string
	Fetch(ctx context.Context, c *http.Client) ([]Job, error)
}

// getJSON performs a GET with a browser-ish UA and decodes JSON into v.
func getJSON(ctx context.Context, c *http.Client, u string, v any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "porto-job-radar/1.0 (+https://github.com/gabrielclteixeira)")
	req.Header.Set("Accept", "application/json")
	resp, err := c.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 256))
		return fmt.Errorf("status %d: %s", resp.StatusCode, strings.TrimSpace(string(body)))
	}
	return json.NewDecoder(resp.Body).Decode(v)
}

// clip trims long descriptions to keep payloads sane.
func clip(s string, n int) string {
	s = strings.TrimSpace(s)
	if len(s) > n {
		return s[:n]
	}
	return s
}

// ---------- Remotive ----------

type remotiveSource struct{ query string }

func (s remotiveSource) Name() string { return "remotive" }
func (s remotiveSource) Fetch(ctx context.Context, c *http.Client) ([]Job, error) {
	u := "https://remotive.com/api/remote-jobs?limit=80&search=" + url.QueryEscape(s.query)
	var out struct {
		Jobs []struct {
			Title           string `json:"title"`
			CompanyName     string `json:"company_name"`
			Location        string `json:"candidate_required_location"`
			URL             string `json:"url"`
			Description     string `json:"description"`
			PublicationDate string `json:"publication_date"`
		} `json:"jobs"`
	}
	if err := getJSON(ctx, c, u, &out); err != nil {
		return nil, err
	}
	jobs := make([]Job, 0, len(out.Jobs))
	for _, j := range out.Jobs {
		jobs = append(jobs, Job{
			Title: j.Title, Company: j.CompanyName, Location: j.Location,
			Remote: "remote", URL: j.URL, Description: clip(j.Description, 4000),
			Source: s.Name(), PostedAt: j.PublicationDate,
		})
	}
	return jobs, nil
}

// ---------- RemoteOK ----------

type remoteOKSource struct{}

func (s remoteOKSource) Name() string { return "remoteok" }
func (s remoteOKSource) Fetch(ctx context.Context, c *http.Client) ([]Job, error) {
	var raw []map[string]any
	if err := getJSON(ctx, c, "https://remoteok.com/api", &raw); err != nil {
		return nil, err
	}
	jobs := make([]Job, 0, len(raw))
	for _, m := range raw {
		if _, ok := m["position"]; !ok {
			continue // first element is metadata
		}
		str := func(k string) string {
			if v, ok := m[k].(string); ok {
				return v
			}
			return ""
		}
		num := func(k string) float64 {
			if v, ok := m[k].(float64); ok {
				return v
			}
			return 0
		}
		title := str("position")
		if title == "" {
			title = str("title")
		}
		sMin, sMax := num("salary_min"), num("salary_max")
		cur := ""
		if sMin > 0 || sMax > 0 {
			cur = "USD" // RemoteOK salaries are USD/year
		}
		jobs = append(jobs, Job{
			Title: title, Company: str("company"), Location: str("location"),
			Remote: "remote", URL: str("url"), Description: clip(str("description"), 4000),
			Source: s.Name(), PostedAt: str("date"),
			SalaryMin: sMin, SalaryMax: sMax, SalaryCurrency: cur,
		})
	}
	return jobs, nil
}

// ---------- Arbeitnow ----------

type arbeitnowSource struct{}

func (s arbeitnowSource) Name() string { return "arbeitnow" }
func (s arbeitnowSource) Fetch(ctx context.Context, c *http.Client) ([]Job, error) {
	var out struct {
		Data []struct {
			Title       string `json:"title"`
			CompanyName string `json:"company_name"`
			Location    string `json:"location"`
			URL         string `json:"url"`
			Description string `json:"description"`
			Remote      bool   `json:"remote"`
			CreatedAt   int64  `json:"created_at"`
		} `json:"data"`
	}
	if err := getJSON(ctx, c, "https://www.arbeitnow.com/api/job-board-api", &out); err != nil {
		return nil, err
	}
	jobs := make([]Job, 0, len(out.Data))
	for _, j := range out.Data {
		remote := "onsite"
		if j.Remote {
			remote = "remote"
		}
		posted := ""
		if j.CreatedAt > 0 {
			posted = time.Unix(j.CreatedAt, 0).UTC().Format("2006-01-02")
		}
		jobs = append(jobs, Job{
			Title: j.Title, Company: j.CompanyName, Location: j.Location,
			Remote: remote, URL: j.URL, Description: clip(j.Description, 4000),
			Source: s.Name(), PostedAt: posted,
		})
	}
	return jobs, nil
}

// ---------- Adzuna (Portugal) ----------

type adzunaSource struct {
	appID, appKey, country, query, where string
}

func (s adzunaSource) Name() string { return "adzuna" }
func (s adzunaSource) Fetch(ctx context.Context, c *http.Client) ([]Job, error) {
	country := s.country
	if country == "" {
		country = "pt"
	}
	u := fmt.Sprintf(
		"https://api.adzuna.com/v1/api/jobs/%s/search/1?app_id=%s&app_key=%s&results_per_page=50&what=%s&where=%s&content-type=application/json",
		country, url.QueryEscape(s.appID), url.QueryEscape(s.appKey),
		url.QueryEscape(s.query), url.QueryEscape(s.where),
	)
	var out struct {
		Results []struct {
			Title     string  `json:"title"`
			Company   struct{ DisplayName string `json:"display_name"` } `json:"company"`
			Location  struct{ DisplayName string `json:"display_name"` } `json:"location"`
			Redirect  string  `json:"redirect_url"`
			Desc      string  `json:"description"`
			Created   string  `json:"created"`
			SalaryMin float64 `json:"salary_min"`
			SalaryMax float64 `json:"salary_max"`
		} `json:"results"`
	}
	if err := getJSON(ctx, c, u, &out); err != nil {
		return nil, err
	}
	jobs := make([]Job, 0, len(out.Results))
	for _, r := range out.Results {
		cur := ""
		if r.SalaryMin > 0 || r.SalaryMax > 0 {
			cur = "EUR" // Adzuna /pt returns annual EUR
		}
		jobs = append(jobs, Job{
			Title: r.Title, Company: r.Company.DisplayName, Location: r.Location.DisplayName,
			Remote: "", URL: r.Redirect, Description: clip(r.Desc, 4000),
			Source: s.Name(), PostedAt: r.Created,
			SalaryMin: r.SalaryMin, SalaryMax: r.SalaryMax, SalaryCurrency: cur,
		})
	}
	return jobs, nil
}

// ---------- Greenhouse (per board token) ----------

type greenhouseSource struct{ token string }

func (s greenhouseSource) Name() string { return "greenhouse:" + s.token }
func (s greenhouseSource) Fetch(ctx context.Context, c *http.Client) ([]Job, error) {
	u := "https://boards-api.greenhouse.io/v1/boards/" + url.PathEscape(s.token) + "/jobs?content=true"
	var out struct {
		Jobs []struct {
			Title    string `json:"title"`
			Location struct{ Name string `json:"name"` } `json:"location"`
			AbsURL   string `json:"absolute_url"`
			Content  string `json:"content"`
			Updated  string `json:"updated_at"`
		} `json:"jobs"`
	}
	if err := getJSON(ctx, c, u, &out); err != nil {
		return nil, err
	}
	jobs := make([]Job, 0, len(out.Jobs))
	for _, j := range out.Jobs {
		jobs = append(jobs, Job{
			Title: j.Title, Company: s.token, Location: j.Location.Name,
			Remote: "", URL: j.AbsURL, Description: clip(j.Content, 4000),
			Source: s.Name(), PostedAt: j.Updated,
		})
	}
	return jobs, nil
}

// ---------- Lever (per company slug) ----------

type leverSource struct{ company string }

func (s leverSource) Name() string { return "lever:" + s.company }
func (s leverSource) Fetch(ctx context.Context, c *http.Client) ([]Job, error) {
	u := "https://api.lever.co/v0/postings/" + url.PathEscape(s.company) + "?mode=json"
	var out []struct {
		Text       string `json:"text"`
		HostedURL  string `json:"hostedUrl"`
		DescPlain  string `json:"descriptionPlain"`
		CreatedAt  int64  `json:"createdAt"`
		Categories struct {
			Location   string `json:"location"`
			Commitment string `json:"commitment"`
			Team       string `json:"team"`
		} `json:"categories"`
	}
	if err := getJSON(ctx, c, u, &out); err != nil {
		return nil, err
	}
	jobs := make([]Job, 0, len(out))
	for _, p := range out {
		posted := ""
		if p.CreatedAt > 0 {
			posted = time.Unix(p.CreatedAt/1000, 0).UTC().Format("2006-01-02")
		}
		jobs = append(jobs, Job{
			Title: p.Text, Company: s.company, Location: p.Categories.Location,
			Remote: "", URL: p.HostedURL, Description: clip(p.DescPlain, 4000),
			Source: s.Name(), PostedAt: posted,
		})
	}
	return jobs, nil
}

// buildSources expands the config into concrete Source instances.
func buildSources(cfg Config) []Source {
	var srcs []Source
	for _, q := range cfg.Queries {
		if cfg.Remotive {
			srcs = append(srcs, remotiveSource{query: q})
		}
		if cfg.Adzuna.AppID != "" && cfg.Adzuna.AppKey != "" {
			srcs = append(srcs, adzunaSource{
				appID: cfg.Adzuna.AppID, appKey: cfg.Adzuna.AppKey,
				country: cfg.Adzuna.Country, query: q, where: cfg.Location,
			})
		}
	}
	if cfg.RemoteOK {
		srcs = append(srcs, remoteOKSource{})
	}
	if cfg.Arbeitnow {
		srcs = append(srcs, arbeitnowSource{})
	}
	for _, t := range cfg.Greenhouse {
		srcs = append(srcs, greenhouseSource{token: t})
	}
	for _, comp := range cfg.Lever {
		srcs = append(srcs, leverSource{company: comp})
	}
	return srcs
}
