using System.Text;

namespace TmIceConverter;

// a conversion's progress in the terminal: one live line at the bottom, other output above it; plain step lines when redirected
internal sealed class ConsoleProgress : IDisposable
{
    private readonly ConvertProgress progress;
    private readonly TextWriter real = Console.Out;
    private readonly BarWriter? writer;
    private int shownStep = -1;

    public ConsoleProgress(ConvertProgress progress)
    {
        this.progress = progress;
        if (!Console.IsOutputRedirected) Console.SetOut(writer = new BarWriter(real));
        progress.Changed += Show;
    }

    private void Show(ConvertProgress.Snapshot now)
    {
        var steps = now.StepNames.Count;
        if (now.Step < 0 || now.Step >= steps) return;
        if (writer is null)
        {
            if (Interlocked.Exchange(ref shownStep, now.Step) != now.Step) lock (real) real.WriteLine($"[{now.Step + 1}/{steps}] {now.StepNames[now.Step]}");
            return;
        }
        const int width = 24;
        var filled = (int)Math.Round(width * now.Percent / 100);
        var count = now.Total > 0 ? $" {now.Done}/{now.Total}" : "";
        var detail = now.Detail is null ? "" : $" ({now.Detail})";
        writer.SetBar($"[{now.Step + 1}/{steps}] {now.StepNames[now.Step]}{count}{detail}  {new string('█', filled)}{new string('░', width - filled)} {now.Percent,3:0}%");
    }

    public void Dispose()
    {
        progress.Changed -= Show;
        if (writer is null) return;
        writer.ClearBar();
        Console.SetOut(real);
    }

    // clears the bar before other text and draws it again after each finished line
    private sealed class BarWriter(TextWriter real) : TextWriter
    {
        private string? bar;
        private bool barDrawn, midLine;

        public override Encoding Encoding => real.Encoding;

        public void SetBar(string line)
        {
            lock (real)
            {
                bar = line;
                if (midLine) return;
                real.Write("\r\x1b[2K" + bar);
                real.Flush();
                barDrawn = true;
            }
        }

        public void ClearBar()
        {
            lock (real)
            {
                if (barDrawn) real.Write("\r\x1b[2K");
                (bar, barDrawn) = (null, false);
                real.Flush();
            }
        }

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (real)
            {
                if (barDrawn) real.Write("\r\x1b[2K");
                real.Write(value);
                midLine = !value.EndsWith('\n');
                barDrawn = false;
                if (!midLine && bar is not null)
                {
                    real.Write(bar);
                    barDrawn = true;
                }
                real.Flush();
            }
        }

        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        public override void WriteLine() => Write("\n");

        public override void Flush() => real.Flush();
    }
}
