namespace TmIceConverter;

// the GUI's conversion: one at a time in the background; the page polls it and finds it again after a reload
internal sealed class ConvertJobs(ConvertCommand convert, ConversionPreview preview)
{
    // State: running | done | failed. Output in Outcome: relative to Maps
    public sealed record Job(string Id, string Map, string Name, string State, ConvertProgress.Snapshot Progress, DateTime Started, DateTime? Ended,
        ConvertCommand.Outcome? Outcome, string? Error);

    private readonly Lock gate = new();
    private Job? job;
    private ConvertProgress? progress;

    public Job? Current
    {
        get
        {
            lock (gate) return job is null ? null : job with { Progress = progress!.Now() };
        }
    }

    // null: one is running already
    public Job? Start(string map, ConvertCommand.Choices choices, string name)
    {
        lock (gate)
        {
            if (job is { State: "running" }) return null;
            progress = new ConvertProgress();
            job = new Job(Guid.NewGuid().ToString("N")[..12], Paths.MapsRelativeOrFull(map), name, "running", progress.Now(), DateTime.UtcNow, null, null, null);
            var (id, steps) = (job.Id, progress);
            _ = Task.Run(async () =>
            {
                ConvertCommand.Outcome? outcome = null;
                string? error = null;
                try
                {
                    // the preview's builds write the same item files
                    if (preview.Building().Running) steps.Start(0, 0, "waiting for the preview's builds");
                    while (preview.Building().Running) await Task.Delay(500);
                    var result = convert.Convert([map], choices, steps).Single();
                    (outcome, error) = (result.Done, result.Error);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"convert {map}: {e}");
                    error = $"{e.GetType().Name}: {e.Message}";
                }
                lock (gate)
                    if (job?.Id == id)
                        job = job with
                        {
                            State = error is null ? "done" : "failed", Ended = DateTime.UtcNow, Error = error,
                            Outcome = outcome is null ? null : outcome with { Output = Paths.MapsRelativeOrFull(outcome.Output) },
                        };
            });
            return job;
        }
    }

    // a finished one the page has shown and let go
    public void Dismiss(string id)
    {
        lock (gate)
            if (job is { State: not "running" } && job.Id == id) job = null;
    }
}
