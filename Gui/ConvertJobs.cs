namespace TmSurfaceEditor;

// the GUI's conversion: one at a time in the background; the page polls it and finds it again after a reload
internal sealed class ConvertJobs(ConvertCommand convert, ConversionPreview preview)
{
    // State: running | done | failed (none converted) | cancelled (Results: the maps saved before). Outcome: the only map's; Results: each map's. Outputs relative to Maps
    public sealed record Job(string Id, string Map, string Name, int Count, string State, ConvertProgress.Snapshot Progress, DateTime Started, DateTime? Ended,
        ConvertCommand.Outcome? Outcome, string? Error, List<Result> Results, bool Cancelling = false);

    public sealed record Result(string Map, string? Output, string? Error);

    private readonly Lock gate = new();
    private Job? job;
    private ConvertProgress? progress;
    private CancellationTokenSource? cancel;

    public Job? Current
    {
        get
        {
            lock (gate) return job is null ? null : job with { Progress = progress!.Now(), Cancelling = job.State == "running" && cancel?.IsCancellationRequested == true };
        }
    }

    // null: one is running already
    public Job? Start(IReadOnlyList<string> maps, ConvertCommand.Choices choices, string name)
    {
        lock (gate)
        {
            if (job is { State: "running" }) return null;
            progress = new ConvertProgress();
            cancel = new CancellationTokenSource();
            var token = cancel.Token;
            job = new Job(Guid.NewGuid().ToString("N")[..12], Paths.MapsRelativeOrFull(maps[0]), name, maps.Count, "running", progress.Now(), DateTime.UtcNow, null, null, null, []);
            var (id, steps) = (job.Id, progress);
            _ = Task.Run(async () =>
            {
                List<ConvertCommand.MapResult> results = [];
                string? error = null;
                try
                {
                    // the preview's builds write the same item files
                    if (preview.Building().Running) steps.Start("Read map", 0, "waiting for the preview's builds");
                    while (preview.Building().Running) await Task.Delay(500, token);
                    results = convert.Convert(maps, choices, steps, token);
                    if (!token.IsCancellationRequested && results.All(r => r.Done is null)) error = maps.Count == 1 ? results.Single().Error : "none of them converted";
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"convert {string.Join(", ", maps)}: {e}");
                    error = $"{e.GetType().Name}: {e.Message}";
                }
                var outcome = maps.Count == 1 ? results.SingleOrDefault()?.Done : null;
                lock (gate)
                    if (job?.Id == id)
                        job = job with
                        {
                            State = token.IsCancellationRequested ? "cancelled" : error is null ? "done" : "failed", Ended = DateTime.UtcNow, Error = error,
                            Outcome = outcome is null ? null : outcome with { Output = Paths.MapsRelativeOrFull(outcome.Output) },
                            Results = [.. results.Select(r => new Result(Paths.MapsRelativeOrFull(r.Map),
                                r.Done is null ? null : Paths.MapsRelativeOrFull(r.Done.Output), r.Error))],
                        };
            });
            return job;
        }
    }

    // stops at the next map or step; builds that started finish first
    public void Cancel(string id)
    {
        lock (gate)
            if (job is { State: "running" } && job.Id == id) cancel?.Cancel();
    }

    // a finished one the page has shown and let go
    public void Dismiss(string id)
    {
        lock (gate)
            if (job is { State: not "running" } && job.Id == id) job = null;
    }
}
