namespace TmIceConverter;

// a conversion's steps as it runs: the terminal draws them, the GUI polls them
public sealed class ConvertProgress
{
    public sealed record Step(string Name, double Weight);

    // weights: rough seconds; the convert sets them per map count (Expect), Build pieces once planning knows how many really build.
    // A step not in the list goes in after the current one: the list only grows forward
    private static readonly Step[] Fixed =
    [
        new("Read map", 1),
        new("Plan pieces", 4),
        new("Build pieces", 1),
        new("Convert and save", 6),
    ];

    public sealed record Snapshot(int Step, int Done, int Total, string? Detail, double Percent, IReadOnlyList<string> StepNames);

    private readonly Lock gate = new();
    private readonly List<Step> steps = [.. Fixed];
    private int step = -1, done, total;
    private string? detail;
    // never goes back: inserted steps add weight
    private double highest;

    public event Action<Snapshot>? Changed;

    // seconds a step is expected to take
    public void Expect(string name, double seconds)
    {
        lock (gate)
            if (steps.FindIndex(s => s.Name == name) is var i and >= 0) steps[i] = steps[i] with { Weight = Math.Max(0.1, seconds) };
    }

    public void Start(string name, int count = 0, string? what = null, double? weight = null)
    {
        lock (gate)
        {
            var index = steps.FindIndex(s => s.Name == name);
            if (index < 0) steps.Insert(index = step + 1, new Step(name, Math.Max(0.1, weight ?? 1)));
            else if (weight is { } w) steps[index] = steps[index] with { Weight = Math.Max(0.1, w) };
            (step, done, total, detail) = (index, 0, count, what);
        }
        Changed?.Invoke(Now());
    }

    public void Advance(int count)
    {
        // workers report out of order
        lock (gate) done = Math.Max(done, count);
        Changed?.Invoke(Now());
    }

    public void Finish()
    {
        lock (gate) (step, done, total, detail) = (steps.Count, 0, 0, null);
        Changed?.Invoke(Now());
    }

    public Snapshot Now()
    {
        lock (gate)
        {
            var all = steps.Sum(s => s.Weight);
            var before = steps.Take(Math.Max(0, step)).Sum(s => s.Weight);
            var inStep = step >= 0 && step < steps.Count && total > 0 ? steps[step].Weight * (double)done / total : 0;
            highest = Math.Max(highest, Math.Min(100, 100 * (before + inStep) / all));
            return new Snapshot(step, done, total, detail, highest, steps.Select(s => s.Name).ToList());
        }
    }
}
