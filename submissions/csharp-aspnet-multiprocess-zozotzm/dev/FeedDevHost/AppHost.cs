var builder = DistributedApplication.CreateBuilder(args);

var pgPassword = builder.AddParameter("pg-password", "feed", secret: true);

var postgres = builder.AddPostgres("postgres", password: pgPassword)
    .WithImageTag("18")
    .WithDataVolume("million-users-pg");

var db = postgres.AddDatabase("feed");

// One-shot: creates feed_seed with the repo's generator, then feed as a copy of it.
// Build it once with: bash dev/seed/build.sh
var seed = builder.AddContainer("seed", "feed-seed:local")
    .WithEnvironment("PGHOST", "postgres")
    .WithEnvironment("PGPORT", "5432")
    .WithEnvironment("PGUSER", "postgres")
    .WithEnvironment("PGPASSWORD", pgPassword)
    .WaitFor(postgres);

// The submission binary. It reads ConnectionStrings__feed from WithReference, or DATABASE_URL.
var api = builder.AddProject<Projects.FeedApi>("api")
    .WithReference(db)
    .WaitForCompletion(seed)
    .WithEnvironment("JWT_SECRET", "million-users-challenge")
    .WithEnvironment("HOST", "0.0.0.0")
    .WithEnvironment("PORT", "3000")
    .WithEnvironment("EXTRA_PORT", "3001")
    .WithEnvironment("WORKERS", "1");

builder.Build().Run();
