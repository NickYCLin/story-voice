using System.Globalization;

namespace StoryVoice.Worker;

internal static class Mp3PacketDuration
{
    public static IReadOnlyList<string> ProbeArguments(string path) =>
        ["-v", "error", "-select_streams", "a:0", "-show_entries", "packet=duration_time", "-of", "default=noprint_wrappers=1", path];

    public static double ParseSeconds(string output)
    {
        decimal seconds = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string prefix = "duration_time=";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!decimal.TryParse(line.AsSpan(prefix.Length), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var duration)
                || duration <= 0 || duration > 2_592_000 - seconds)
                return 0;
            seconds += duration;
        }

        return (double)seconds;
    }
}
