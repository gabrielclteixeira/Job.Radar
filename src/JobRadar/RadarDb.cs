using Microsoft.EntityFrameworkCore;

namespace JobRadar;

/// <summary>SQLite store for jobs + analysis. Uses EnsureCreated (no migrations).</summary>
public class RadarDb : DbContext
{
    private readonly string _path;
    public RadarDb(string path) => _path = path;

    public DbSet<JobEntity> Jobs => Set<JobEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={_path}");

    protected override void OnModelCreating(ModelBuilder b)
        => b.Entity<JobEntity>().HasIndex(j => j.Key).IsUnique();
}
