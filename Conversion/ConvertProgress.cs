namespace TmIceConverter;

// a conversion's steps as it runs: the terminal draws them, the GUI polls them
public sealed class ConvertProgress
{
    public sealed record Step(string Name, int Weight);

    // weights: rough share of a typical run's time; building pieces dominates when there are any
    public static readonly Step[] Steps =
    [
        new("Read map", 2),
        new("Plan pieces", 8),
        new("Build pieces", 70),
        new("Convert", 12),
        new("Save", 8),
    ];

    public sealed record Snapshot(int Step, int Done, int Total, string? Detail, double Percent, IReadOnlyList<string> StepNames);

    private readonly Lock gate = new();
    private int step = -1, done, total;
    private string? detail;
    // never goes back: planning runs again after a build
    private double highest;

    public event Action<Snapshot>? Changed;

    public void Start(int index, int count = 0, string? what = null)
    {
        lock (gate) (step, done, total, detail) = (index, 0, count, what);
        Changed?.Invoke(Now());
    }

    public void Advance(int count)
    {
        lock (gate) done = count;
        Changed?.Invoke(Now());
    }

    public void Finish()
    {
        lock (gate) (step, done, total, detail) = (Steps.Length, 0, 0, null);
        Changed?.Invoke(Now());
    }

    public Snapshot Now()
    {
        lock (gate)
        {
            var all = Steps.Sum(s => s.Weight);
            var before = Steps.Take(Math.Max(0, step)).Sum(s => s.Weight);
            var inStep = step >= 0 && step < Steps.Length && total > 0 ? Steps[step].Weight * (double)done / total : 0;
            highest = Math.Max(highest, Math.Min(100, 100 * (before + inStep) / all));
            return new Snapshot(step, done, total, detail, highest, Steps.Select(s => s.Name).ToList());
        }
    }
}
