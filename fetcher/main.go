// Command fetcher concurrently pulls jobs from several public job APIs/ATS
// boards, normalizes them, de-duplicates, and prints a JSON array to stdout.
// Logs (per-source counts and errors) go to stderr so stdout stays clean.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"log"
	"net/http"
	"os"
	"sort"
	"strings"
	"sync"
	"time"
)

func main() {
	configPath := flag.String("config", "fetcher-config.json", "path to fetcher config JSON")
	timeout := flag.Duration("timeout", 45*time.Second, "overall fetch timeout")
	flag.Parse()
	log.SetOutput(os.Stderr)
	log.SetFlags(0)

	cfg, err := loadConfig(*configPath)
	if err != nil {
		log.Fatalf("config: %v", err)
	}

	sources := buildSources(cfg)
	if len(sources) == 0 {
		log.Fatal("no sources enabled — check your config")
	}

	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	client := &http.Client{Timeout: 30 * time.Second}

	var (
		mu  sync.Mutex
		all []Job
		wg  sync.WaitGroup
	)
	for _, s := range sources {
		wg.Add(1)
		go func(src Source) {
			defer wg.Done()
			jobs, err := src.Fetch(ctx, client)
			if err != nil {
				log.Printf("  [%-22s] error: %v", src.Name(), err)
				return
			}
			log.Printf("  [%-22s] %d jobs", src.Name(), len(jobs))
			mu.Lock()
			all = append(all, jobs...)
			mu.Unlock()
		}(s)
	}
	wg.Wait()

	deduped := dedupe(all)
	log.Printf("total: %d raw -> %d unique", len(all), len(deduped))

	enc := json.NewEncoder(os.Stdout)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(deduped); err != nil {
		log.Fatalf("encode: %v", err)
	}
}

func loadConfig(path string) (Config, error) {
	var cfg Config
	b, err := os.ReadFile(path)
	if err != nil {
		return cfg, err
	}
	if err := json.Unmarshal(b, &cfg); err != nil {
		return cfg, err
	}
	return cfg, nil
}

// dedupe removes duplicates by normalized URL, falling back to title+company.
func dedupe(jobs []Job) []Job {
	seen := make(map[string]bool, len(jobs))
	out := make([]Job, 0, len(jobs))
	for _, j := range jobs {
		key := strings.ToLower(strings.TrimSpace(j.URL))
		if key == "" {
			key = strings.ToLower(strings.TrimSpace(j.Title + "|" + j.Company))
		}
		if seen[key] {
			continue
		}
		seen[key] = true
		out = append(out, j)
	}
	sort.Slice(out, func(i, k int) bool { return out[i].PostedAt > out[k].PostedAt })
	return out
}
