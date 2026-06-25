using System.Diagnostics;

namespace JobRadar;

/// <summary>
/// Runs the Go fetcher to (re)generate jobs.raw.json. Prefers a prebuilt
/// fetcher.exe; falls back to `go run`. If neither is available it leaves any
/// existing jobs file in place so the rest of the pipeline can still run.
/// </summary>
public static class FetcherRunner
{
    public static async Task EnsureJobsAsync(string root, string configPath, string rawPath,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);

        string exe = Path.Combine(root, OperatingSystem.IsWindows() ? "fetcher.exe" : "fetcher");
        string fetcherDir = Path.Combine(root, "fetcher");

        ProcessStartInfo? psi = null;
        if (File.Exists(exe))
        {
            psi = new ProcessStartInfo { FileName = exe, WorkingDirectory = root };
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(configPath);
        }
        else if (Directory.Exists(fetcherDir))
        {
            psi = new ProcessStartInfo { FileName = "go", WorkingDirectory = fetcherDir };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(".");
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(configPath);
        }

        if (psi is null)
        {
            L("Fetcher não encontrado — a usar o jobs.raw.json existente, se houver.");
            return;
        }

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) { L("Não foi possível iniciar o fetcher."); return; }
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            string jobs = await stdout;
            _ = await stderr;
            if (!string.IsNullOrWhiteSpace(jobs))
                await File.WriteAllTextAsync(rawPath, jobs, ct);
        }
        catch (Exception ex)
        {
            L($"Fetcher falhou ({ex.Message}) — a usar jobs.raw.json existente, se houver.");
        }
    }
}
