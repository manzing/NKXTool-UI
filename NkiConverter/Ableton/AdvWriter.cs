using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text;
using NkiConverter.Audio;
using NkiConverter.Kontakt;
using NkiConverter.Model;

namespace NkiConverter.Ableton;

/// <summary>A sample file which has been written to the output library.</summary>
public sealed record WrittenSample(string File, string RelativePath, AudioFileInfo Info);

/// <summary>Creates Ableton Live Sampler presets (.adv = gzip compressed XML).</summary>
internal static class AdvWriter
{
    private const double DefaultReleaseSeconds = 0.01;

    public static void Write(
        Instrument instrument,
        IReadOnlyDictionary<SampleZone, WrittenSample> samples,
        string presetFile,
        int abletonVersion,
        List<string> warnings)
    {
        var isLive12 = abletonVersion >= 12;
        var zones = new List<(SampleGroup Group, SampleZone Zone)>();
        var releaseZones = 0;
        foreach (var group in instrument.Groups)
        {
            foreach (var zone in group.Zones)
            {
                if (!samples.ContainsKey(zone))
                {
                    continue;
                }

                if (group.IsReleaseTrigger)
                {
                    releaseZones++;
                    continue;
                }

                zones.Add((group, zone));
            }
        }

        if (releaseZones > 0)
        {
            warnings.Add($"{releaseZones} zone(s) of release-trigger groups were left out: Ableton Sampler cannot trigger samples on note-off.");
        }

        if (zones.Count == 0)
        {
            throw new InvalidDataException("No playable zones left to write.");
        }

        if (zones.Any(z => z.Zone.Reverse))
        {
            warnings.Add("Some groups play their samples reversed in Kontakt; Ableton Sampler has no per-zone reverse, they play forwards.");
        }

        if (zones.Any(z => z.Zone.OneShot))
        {
            warnings.Add("Some zones are one-shot in Kontakt; Ableton Sampler stops them at note-off according to the amp envelope.");
        }

        var hasRoundRobin = zones.Any(z => z.Group.PlayLogic != PlayLogic.Always);
        var nativeRoundRobin = isLive12 && hasRoundRobin && zones.All(z => z.Group.PlayLogic != PlayLogic.Always);
        var selectorRanges = hasRoundRobin && !nativeRoundRobin ? CalculateSelectorRanges(zones) : [];
        if (selectorRanges.Count > 0)
        {
            warnings.Add("Round-robin groups were spread over the Sampler's Selector range. Modulate the Selector to cycle through them"
                         + (isLive12 ? "." : " (or convert for Live 12 to use native round robin)."));
        }

        var partTemplate = LoadTemplate("ADV-MultiSamplePart-Template.xml");
        var parts = new StringBuilder();
        for (var i = 0; i < zones.Count; i++)
        {
            if (i > 0)
            {
                parts.Append('\n');
            }

            var (_, zone) = zones[i];
            selectorRanges.TryGetValue(zone, out var selector);
            parts.Append(CreatePart(partTemplate, i, zone, samples[zone], selector));
        }

        var text = LoadTemplate(isLive12 ? "ADV12-Template.xml" : "ADV11-Template.xml");
        var values = new Dictionary<string, string>
        {
            ["PRESET_NAME"] = Escape(instrument.Name.Replace('.', '_')),
            ["FILE_NAME"] = Escape(Path.GetFileName(presetFile)),
            ["PRESET_ABSOLUTE_PATH"] = Escape(ToAbletonPath(presetFile)),
            ["ANNOTATION"] = Escape(CreateAnnotation(instrument)),
            ["MULTI_SAMPLE_PARTS"] = parts.ToString(),
            ["ROUND_ROBIN"] = nativeRoundRobin ? "true" : "false",
            ["ROUND_ROBIN_MODE"] = zones.Any(z => z.Group.PlayLogic == PlayLogic.Random) ? "3" : "0",
            ["PITCHBEND_RANGE"] = instrument.PitchBendRange.ToString(CultureInfo.InvariantCulture),
            ["VOLUME_VELOCITY_MOD"] = Format(instrument.VelocityToVolume ?? 0)
        };

        // Amplitude envelope (Ableton: times in ms, levels linear)
        var amp = instrument.AmpEnvelope ?? new Envelope { Sustain = 1, Release = DefaultReleaseSeconds };
        values["AMP_EG_ATTACK_TIME"] = FormatTime(amp.Attack);
        values["AMP_EG_DECAY_TIME"] = FormatTime(amp.Hold + amp.Decay);
        values["AMP_EG_RELEASE_TIME"] = FormatTime(amp.Release);
        values["AMP_EG_START_LEVEL"] = "0";
        values["AMP_EG_HOLD_LEVEL"] = "1";
        values["AMP_EG_SUSTAIN_LEVEL"] = Format(Math.Clamp(amp.Sustain, 0, 1));
        values["AMP_EG_END_LEVEL"] = "0";
        values["AMP_EG_ATTACK_SLOPE"] = Format(Math.Clamp(-amp.AttackSlope, -1, 1));
        values["AMP_EG_DECAY_SLOPE"] = "0";
        values["AMP_EG_RELEASE_SLOPE"] = "0";

        // Pitch envelope and filter are not converted: disable them with neutral values
        values["PITCH_EG_ENABLED"] = "false";
        values["PITCH_EG_AMOUNT"] = "0";
        values["FILTER_ENABLED"] = "false";
        values["FILTER_TYPE"] = "0";
        values["FILTER_SLOPE"] = "true";
        values["FILTER_FREQ"] = "22000";
        values["FILTER_RES"] = "0";
        values["FILTER_EG_ENABLED"] = "false";
        values["FILTER_EG_AMOUNT"] = "0";
        values["FILTER_VELOCITY_MOD"] = "0";
        values["FILTER_KEY_MOD"] = "0";
        foreach (var prefix in new[] { "PITCH_EG", "FILTER_EG" })
        {
            values.TryAdd($"{prefix}_ATTACK_TIME", "0");
            values.TryAdd($"{prefix}_DECAY_TIME", "600");
            values.TryAdd($"{prefix}_RELEASE_TIME", "50");
            values.TryAdd($"{prefix}_START_LEVEL", "0");
            values.TryAdd($"{prefix}_HOLD_LEVEL", "1");
            values.TryAdd($"{prefix}_SUSTAIN_LEVEL", "0");
            values.TryAdd($"{prefix}_END_LEVEL", "0");
            values.TryAdd($"{prefix}_ATTACK_SLOPE", "0");
            values.TryAdd($"{prefix}_DECAY_SLOPE", "0");
            values.TryAdd($"{prefix}_RELEASE_SLOPE", "0");
        }

        text = ReplacePlaceholders(text, values);

        Directory.CreateDirectory(Path.GetDirectoryName(presetFile)!);
        using var file = File.Create(presetFile);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        gzip.Write(bytes, 0, bytes.Length);
    }

    private static string CreatePart(string template, int id, SampleZone zone, WrittenSample sample, (int Low, int High) selector)
    {
        var frames = sample.Info.Frames;

        var start = Math.Clamp(zone.SampleStart, 0, Math.Max(0, frames - 1));
        var end = frames;
        if (zone.SampleEnd > 0)
        {
            end = Math.Min(zone.SampleEnd, frames);
        }
        else if (zone.SampleEndOffsetFromEnd > 0)
        {
            end = frames - zone.SampleEndOffsetFromEnd;
        }

        if (end <= start)
        {
            end = frames;
        }

        // Kontakt crossfades lie inside the zone: the full-volume range shrinks, the outer borders stay
        var keyLow = Math.Min(zone.KeyLow, zone.KeyHigh);
        var keyHigh = Math.Max(zone.KeyLow, zone.KeyHigh);
        var keyMin = Math.Min(keyLow + Math.Max(0, zone.KeyFadeLow), keyHigh);
        var keyMax = Math.Max(keyHigh - Math.Max(0, zone.KeyFadeHigh), keyMin);
        var velLow = Math.Clamp(Math.Min(zone.VelocityLow, zone.VelocityHigh), 1, 127);
        var velHigh = Math.Clamp(Math.Max(zone.VelocityLow, zone.VelocityHigh), 1, 127);
        var velMin = Math.Min(velLow + Math.Max(0, zone.VelocityFadeLow), velHigh);
        var velMax = Math.Max(velHigh - Math.Max(0, zone.VelocityFadeHigh), velMin);

        // Split the tuning into semitones (moved into the root key) and cents
        var semitones = (int)Math.Round(zone.Tuning);
        var cents = (int)Math.Round((zone.Tuning - semitones) * 100);
        var rootKey = Math.Clamp(zone.KeyRoot - semitones, 0, 127);

        var hasSelector = selector != default;
        var values = new Dictionary<string, string>
        {
            ["SAMPLE_ID"] = id.ToString(CultureInfo.InvariantCulture),
            ["ZONE_NAME"] = Escape(Path.GetFileNameWithoutExtension(sample.File)),
            ["SAMPLE_RELATIVE_PATH"] = Escape(sample.RelativePath),
            ["SAMPLE_ABSOLUTE_PATH"] = Escape(ToAbletonPath(sample.File)),
            ["SAMPLE_START"] = Int(start),
            ["SAMPLE_END"] = Int(end),
            ["SAMPLE_FILE_SIZE"] = new FileInfo(sample.File).Length.ToString(CultureInfo.InvariantCulture),
            ["SAMPLE_FILE_TIMESTAMP"] = new DateTimeOffset(File.GetLastWriteTimeUtc(sample.File)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["SAMPLE_RATE"] = Int(sample.Info.SampleRate),
            ["SAMPLE_DURATION"] = Int(frames),
            ["KEY_RANGE_LOW"] = Int(keyMin),
            ["KEY_RANGE_HIGH"] = Int(keyMax),
            ["KEY_RANGE_LOW_CROSSFADE"] = Int(keyLow),
            ["KEY_RANGE_HIGH_CROSSFADE"] = Int(keyHigh),
            ["VEL_RANGE_LOW"] = Int(velMin),
            ["VEL_RANGE_HIGH"] = Int(velMax),
            ["VEL_RANGE_LOW_CROSSFADE"] = Int(velLow),
            ["VEL_RANGE_HIGH_CROSSFADE"] = Int(velHigh),
            ["SELECTOR_LOW"] = Int(hasSelector ? selector.Low : 0),
            ["SELECTOR_HIGH"] = Int(hasSelector ? selector.High : 127),
            ["ROOT_KEY"] = Int(rootKey),
            ["DETUNE"] = Int(cents),
            ["TUNE_SCALE"] = zone.KeyTracking ? "100" : "0",
            ["PANORAMA"] = Format(Math.Clamp(zone.Pan, -1, 1)),
            ["VOLUME"] = Format(Units.DbToValue(zone.GainDb))
        };

        var loop = zone.Loops.FirstOrDefault(l => l.End > l.Start && l.Start < end);
        if (loop is null)
        {
            values["LOOP_MODE"] = "0";
            values["SAMPLE_RELEASE_LOOP"] = "3";
            values["LOOP_START"] = "0";
            values["LOOP_END"] = Int(end);
            values["LOOP_CROSSFADE"] = "0";
            values["LOOP_DETUNE"] = "0";
        }
        else
        {
            var loopStart = Math.Clamp(loop.Start, 0, end);
            var loopEnd = Math.Clamp(loop.End, loopStart + 1, end);
            values["LOOP_MODE"] = loop.Type == LoopType.Alternating ? "2" : "1";
            values["SAMPLE_RELEASE_LOOP"] = loop.UntilRelease ? "0" : "3";
            values["LOOP_START"] = Int(loopStart);
            values["LOOP_END"] = Int(loopEnd);
            values["LOOP_CROSSFADE"] = Int(Math.Min(loop.CrossfadeSamples, loopEnd - loopStart));
            values["LOOP_DETUNE"] = Int((int)Math.Round(loop.Tuning * 100));
        }

        return ReplacePlaceholders(template, values);
    }

    /// <summary>Spreads round robin zones over the selector range 0..127 (used when native round robin is not available).</summary>
    private static Dictionary<SampleZone, (int Low, int High)> CalculateSelectorRanges(List<(SampleGroup Group, SampleZone Zone)> zones)
    {
        var result = new Dictionary<SampleZone, (int, int)>();
        var cycling = zones.Where(z => z.Group.PlayLogic != PlayLogic.Always).ToList();
        var positions = cycling.Select(z => Math.Max(1, z.Group.RoundRobinPosition)).Distinct().Order().ToList();

        if (positions.Count > 1)
        {
            foreach (var (group, zone) in cycling)
            {
                result[zone] = Slice(positions.IndexOf(Math.Max(1, group.RoundRobinPosition)), positions.Count);
            }

            return result;
        }

        var groups = cycling.Select(z => z.Group).Distinct().ToList();
        if (groups.Count > 1)
        {
            foreach (var (group, zone) in cycling)
            {
                result[zone] = Slice(groups.IndexOf(group), groups.Count);
            }
        }

        return result;
    }

    private static (int, int) Slice(int index, int count) => (index * 128 / count, (index + 1) * 128 / count - 1);

    private static string CreateAnnotation(Instrument instrument)
    {
        var m = instrument.Metadata;
        var lines = new List<string> { $"Converted from Kontakt instrument \"{instrument.Name}\" ({instrument.SourceFormat})." };
        if (!string.IsNullOrWhiteSpace(m.Library))
        {
            lines.Add($"Library: {m.Library}");
        }

        if (!string.IsNullOrWhiteSpace(m.Vendor))
        {
            lines.Add($"Vendor: {m.Vendor}");
        }

        if (!string.IsNullOrWhiteSpace(m.Author))
        {
            lines.Add($"Author: {m.Author}");
        }

        if (!string.IsNullOrWhiteSpace(m.Category))
        {
            lines.Add($"Category: {m.Category}");
        }

        if (m.Tags.Count > 0)
        {
            lines.Add($"Tags: {string.Join(", ", m.Tags)}");
        }

        if (!string.IsNullOrWhiteSpace(m.Url))
        {
            lines.Add($"Website: {m.Url}");
        }

        if (!string.IsNullOrWhiteSpace(m.Description))
        {
            lines.Add(string.Empty);
            lines.Add(m.Description.Trim());
        }

        lines.Add(string.Empty);
        lines.Add($"Source: {Path.GetFileName(instrument.SourceFile)}");
        return string.Join("\n", lines);
    }

    private static string ReplacePlaceholders(string template, Dictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length + 1024);
        var pos = 0;
        while (pos < template.Length)
        {
            var start = template.IndexOf('%', pos);
            if (start < 0)
            {
                sb.Append(template, pos, template.Length - pos);
                break;
            }

            var end = template.IndexOf('%', start + 1);
            if (end < 0)
            {
                sb.Append(template, pos, template.Length - pos);
                break;
            }

            var key = template.Substring(start + 1, end - start - 1);
            if (key.Length > 0 && key.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                if (!values.TryGetValue(key, out var value))
                {
                    throw new InvalidOperationException($"No value for template placeholder %{key}%.");
                }

                sb.Append(template, pos, start - pos).Append(value);
                pos = end + 1;
            }
            else
            {
                sb.Append(template, pos, end - pos);
                pos = end;
            }
        }

        return sb.ToString();
    }

    private static string LoadTemplate(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("NkiConverter.Templates." + name)
                           ?? throw new InvalidOperationException($"Template {name} not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Ableton stores paths with forward slashes, also on Windows (e.g. "C:/Users/...").</summary>
    private static string ToAbletonPath(string file) => Path.GetFullPath(file).Replace('\\', '/');

    private static string Escape(string text)
    {
        var escaped = SecurityElement.Escape(text) ?? string.Empty;
        return escaped.Replace("\r", string.Empty).Replace("\n", "&#10;").Replace("\t", "&#9;");
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    private static string FormatTime(double seconds) => Format(Math.Max(0, seconds) * 1000.0);
}
