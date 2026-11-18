using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace UnityMetalCli;

internal static class TraceProfile
{
    internal static object Analyze(string unityPath, string runtimePath, double warmup, double? seconds,
                                   (int First, int Last)? range)
    {
        var unity = Read(unityPath);
        var runtime = Read(runtimePath);
        if (range is { } bounds)
            unity = unity.Where(row => row.Tick("unity_frame") >= bounds.First && row.Tick("unity_frame") <= bounds.Last).ToList();

        long[] displayTimes = unity.Select(row => row.Tick("xr_display_ns")).Where(time => time > 0).ToArray();
        if (displayTimes.Length == 0)
            throw new InvalidDataException("No Unity rows have submitted OpenXR display times.");
        long start = checked(displayTimes[0] + (long)(warmup * 1e9));
        long end = displayTimes[^1];
        if (seconds is { } duration)
        {
            long desired = checked(start + (long)(duration * 1e9));
            if (desired > end)
                throw new InvalidDataException("There are fewer samples than requested for this window.");
            end = desired;
        }
        bool Inside(long time) => time >= start && time <= end;
        unity = unity.Where(row => Inside(row.Tick("xr_display_ns"))).ToList();
        runtime = runtime.Where(row => Inside(row.Tick("display_ns"))).ToList();
        if (unity.Count == 0 || runtime.Count == 0)
            throw new InvalidDataException("Unity and MacVR are missing overlapping samples for this interval.");

        double[] periods = Numbers(unity, "display_period_ns", 1e-6).Order().ToArray();
        if (periods.Length == 0)
            throw new InvalidDataException("No headset frame period is available in this interval.");
        double budget = (periods[(periods.Length - 1) / 2] + periods[periods.Length / 2]) / 2;
        if (!double.IsFinite(budget) || budget <= 0)
            throw new InvalidDataException("Invalid headset frame period.");

        Row[] submitted = runtime.Where(row => row["kind"] == "submit").ToArray();
        Row[] completed = runtime.Where(row => row["kind"] == "complete" && row["status"] == "4").ToArray();
        Row[] waits = runtime.Where(row => row["kind"] == "wait").ToArray();
        // A render completion can occur once but be sampled by several Unity loops.
        Row[] unique = unity.Where(row => row.Tick("completed_sequence") > 0)
            .GroupBy(row => row.Tick("completed_sequence")).Select(group => group.Last()).ToArray();
        long[] memory = unity.Where(row => row.Has("system_used_bytes")).Select(row => row.Tick("system_used_bytes")).ToArray();

        var metrics = new Dictionary<string, IEnumerable<double>>
        {
            ["unity_loop"] = Numbers(unity, "loop_ms"),
            ["unity_main_thread"] = Numbers(unity, "main_thread_ns", 1e-6),
            ["unity_render_thread"] = Numbers(unity, "render_thread_ns", 1e-6),
            ["unity_eye_render_cpu"] = Numbers(unity, "eye_render_cpu_ms"),
            ["unity_openxr_wait"] = Numbers(unity, "xr_wait_ms"),
            ["unity_metal_command_buffer"] = Numbers(unique, "unity_metal_gpu_ns", 1e-6),
            ["openxr_end_frame_cpu"] = Numbers(unique, "end_frame_ns", 1e-6),
            ["macvr_submit_cpu"] = Numbers(submitted, "submit_cpu_ns", 1e-6),
            ["macvr_metal_gpu"] = Numbers(completed, "gpu_ns", 1e-6),
            ["macvr_gpu_completion_interval"] = Intervals(completed, "event_ns")
        };
        return new
        {
            window_display_start_ns = start,
            window_display_end_ns = end,
            headset_budget_ms = budget,
            timings = metrics.ToDictionary(metric => metric.Key, metric => Summarize(metric.Value, budget)),
            unity_loop_count = unity.Count,
            openxr_stereo_completed = unique.Length,
            macvr_submitted = submitted.Length,
            macvr_gpu_completed = completed.Length,
            macvr_missed_wait_slots = waits.Sum(row => row.Tick("slot_misses")),
            native_display_gpu_completions = completed.Sum(row => row.Tick("native")),
            repeated_physical_presentations = (int?)null,
            unity_system_used_bytes = memory.Length == 0 ? null : new
            {
                first = memory[0],
                last = memory[^1],
                delta = memory[^1] - memory[0],
                peak = memory.Max()
            }
        };
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        double point = (sorted.Length - 1) * fraction;
        int lower = (int)Math.Floor(point);
        int upper = (int)Math.Ceiling(point);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (point - lower);
    }

    // Fixed-point formatting rounds the original binary double to four places.
    // Math.Round scales by 10,000 first, which can turn a near midpoint into a tie.
    private static double FourPlaces(double value) =>
        double.Parse(value.ToString("F4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static object? Summarize(IEnumerable<double> values, double budget)
    {
        double[] sorted = values.Where(value => value > 0 && double.IsFinite(value)).Order().ToArray();
        if (sorted.Length == 0)
            return null;
        return new
        {
            samples = sorted.Length,
            p50_ms = FourPlaces(Percentile(sorted, .50)),
            p95_ms = FourPlaces(Percentile(sorted, .95)),
            p99_ms = FourPlaces(Percentile(sorted, .99)),
            over_budget = sorted.Count(value => value > budget)
        };
    }

    private static IEnumerable<double> Numbers(IEnumerable<Row> rows, string key, double scale = 1) =>
        rows.Where(row => row.Has(key)).Select(row => double.Parse(row[key], CultureInfo.InvariantCulture) * scale);

    private static IEnumerable<double> Intervals(IEnumerable<Row> rows, string column)
    {
        long[] timestamps = rows.Select(row => row.Tick(column)).ToArray();
        return timestamps.Zip(timestamps.Skip(1), (first, second) => (second - first) / 1e6);
    }

    private static List<Row> Read(string path)
    {
        using var csv = new TextFieldParser(path)
        {
            TextFieldType = FieldType.Delimited,
            CommentTokens = ["#"],
            TrimWhiteSpace = false
        };
        csv.SetDelimiters(",");
        string[] ReadFields()
        {
            try { return csv.ReadFields() ?? []; }
            catch (MalformedLineException error)
            {
                throw new InvalidDataException($"Malformed CSV at line {csv.ErrorLineNumber} in {path}.", error);
            }
        }
        string[] header = ReadFields();
        if (header.Length == 0)
            throw new InvalidDataException("Empty CSV: " + path);
        var rows = new List<Row>();
        while (!csv.EndOfData)
        {
            string[] values = ReadFields();
            if (values.Length != header.Length)
                throw new InvalidDataException($"Invalid field count in {path} at line {csv.LineNumber - 1}.");
            rows.Add(new(header.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal)));
        }
        return rows;
    }

    private sealed record Row(Dictionary<string, string> Fields)
    {
        public string this[string key] => Fields[key];
        public long Tick(string key) => long.Parse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture);
        public bool Has(string key) => Fields.TryGetValue(key, out string? cell) && cell.Length != 0;
    }
}
