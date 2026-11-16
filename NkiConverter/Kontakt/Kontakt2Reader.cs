using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NkiConverter.IO;
using NkiConverter.Model;

namespace NkiConverter.Kontakt;

/// <summary>
/// Reads NKI files of Kontakt 2 up to 4.1 (header + zlib compressed XML) and Kontakt 4.2 (header +
/// FastLZ compressed binary preset data, the same structure which Kontakt 5+ uses).
/// </summary>
internal static partial class Kontakt2Reader
{
    private const int HeaderKontakt42 = 0x110;

    public static List<Instrument> Read(byte[] fileData, string sourceFile, bool bigEndian)
    {
        var r = new ByteReader(fileData, bigEndian);
        r.U32(); // file magic
        var compressedLength = (int)r.U32();
        var headerVersion = r.U16();
        var isKontakt42 = headerVersion == HeaderKontakt42;
        r.U32(); // header magic
        r.U16(); // patch type
        var version = r.Bytes(4);
        r.Ascii(4); // library ID
        r.U32(); // creation timestamp
        r.U32();
        r.U16(); // zones
        r.U16(); // groups
        r.U16(); // instruments
        r.U32(); // sample size
        var isMonolith = r.U32() == 1;
        r.U32(); // min supported version
        r.U32();
        r.U32(); // icon
        var author = r.Latin1(8).Trim('\0', ' ');
        r.Skip(3); // categories
        var website = r.Ascii(86).Trim('\0', ' ');
        r.Skip(3);
        r.U32(); // flags
        r.Skip(isKontakt42 ? 16 : 4); // MD5 or checksum
        r.U32(); // SVN revision

        var kontaktVersion = FormatVersion(version, bigEndian);
        List<Instrument> instruments;
        if (isKontakt42)
        {
            r.U32(); // CRC32 of the compressed data
            var decompressedLength = (int)r.U32();
            r.Skip(32);
            var compressed = r.Bytes(Math.Min(compressedLength, r.Remaining));
            var presetData = FastLz.Decompress(compressed, decompressedLength);
            instruments = Kontakt5Reader.ReadPresetData(presetData, sourceFile, $"Kontakt 4.2 ({kontaktVersion})");
        }
        else
        {
            if (isMonolith)
            {
                throw new NotSupportedException("Kontakt 2-4 monolith NKI files (samples embedded in the NKI) are not supported. Save the instrument with external samples first.");
            }

            var xml = InflateZlib(fileData, r.Position);
            instruments = ParseXml(xml, sourceFile, $"Kontakt 2-4.1 ({kontaktVersion})");
        }

        foreach (var instrument in instruments)
        {
            if (string.IsNullOrWhiteSpace(instrument.Metadata.Author) && author.Length > 0 && author != "(null)")
            {
                instrument.Metadata.Author = author;
            }

            if (string.IsNullOrWhiteSpace(instrument.Metadata.Url) && website.Length > 0 && website != "(null)")
            {
                instrument.Metadata.Url = website;
            }
        }

        return instruments;
    }

    private static string FormatVersion(byte[] version, bool bigEndian)
    {
        var v = bigEndian ? version : version.Reverse().ToArray();
        return $"{v[0]}.{v[1]}.{v[2]}.{v[3]}";
    }

    private static string InflateZlib(byte[] data, int offset)
    {
        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var bytes = output.ToArray();

        // The XML is UTF-8 encoded; strip a possible BOM and trailing zeros
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿').TrimEnd('\0', ' ', '\r', '\n');
        return text;
    }

    // ------------------------------------------------------------------------------------------
    // XML (Kontakt 2 - 4.1)
    // ------------------------------------------------------------------------------------------

    internal static List<Instrument> ParseXml(string xml, string sourceFile, string formatName)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var top = document.Root ?? throw new InvalidDataException("Empty instrument XML.");

        var containers = top.Name.LocalName == "K2_Container"
            ? [top]
            : top.Descendants("K2_Container").ToList();

        var programs = containers
            .Select(c => c.Element("Programs"))
            .Where(p => p is not null)
            .SelectMany(p => p!.Elements("K2_Program"))
            .ToList();

        if (programs.Count == 0)
        {
            throw new InvalidDataException("No Kontakt program found in the instrument XML.");
        }

        return programs.Select(p => ParseProgram(p, sourceFile, formatName)).ToList();
    }

    private static Instrument ParseProgram(XElement program, string sourceFile, string formatName)
    {
        var instrument = new Instrument
        {
            Name = (string?)program.Attribute("name") ?? Path.GetFileNameWithoutExtension(sourceFile),
            SourceFile = sourceFile,
            SourceFormat = formatName
        };

        var isAdditiveTuning = double.TryParse((string?)program.Attribute("version"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0.8;
        var programParams = Values(program.Element("Parameters"));
        var programVolume = GetDouble(programParams, "volume", 1);
        var programPan = GetDouble(programParams, "pan", 0);
        var programTune = ToTuneRatio(GetDouble(programParams, "tune", isAdditiveTuning ? 0 : 1), isAdditiveTuning);

        var polyphony = program.Element("Polyphony");
        if (polyphony is not null)
        {
            foreach (var voiceGroup in polyphony.Elements("VoiceGroup"))
            {
                if ((string?)voiceGroup.Attribute("index") == "0" && int.TryParse(Values(voiceGroup).GetValueOrDefault("maxNumVoices"), out var voices) && voices > 0)
                {
                    instrument.MaxVoices = voices;
                }
            }
        }

        var zoneElements = program.Element("Zones")?.Elements("K2_Zone").ToList() ?? [];
        var groupElements = program.Element("Groups")?.Elements("K2_Group").ToList() ?? [];

        foreach (var groupElement in groupElements)
        {
            var startCriteria = (groupElement.Element("GroupStart")?.Elements("StartCriteria") ?? [])
                .Select(Values)
                .Where(values => values.Count > 0)
                .ToList();
            if (startCriteria.Any(sc => sc.GetValueOrDefault("sequencerOnly") == "yes"))
            {
                continue;
            }

            var groupParams = Values(groupElement.Element("Parameters"));
            var group = new SampleGroup
            {
                Name = (string?)groupElement.Attribute("name") ?? string.Empty,
                IsReleaseTrigger = groupParams.GetValueOrDefault("releaseTrigger") == "yes"
            };

            foreach (var sc in startCriteria)
            {
                var mode = sc.GetValueOrDefault("mode");
                if (mode is not null && mode.StartsWith("cycle", StringComparison.Ordinal))
                {
                    group.PlayLogic = mode == "cycle_random" ? PlayLogic.Random : PlayLogic.RoundRobin;
                    if (int.TryParse(sc.GetValueOrDefault("cycleClass"), out var cycleClass) && cycleClass >= 0)
                    {
                        group.RoundRobinPosition = cycleClass + 1;
                    }

                    break;
                }
            }

            var groupVolume = GetDouble(groupParams, "volume", 1);
            var groupPan = GetDouble(groupParams, "pan", 0);
            var groupTune = ToTuneRatio(GetDouble(groupParams, "tune", isAdditiveTuning ? 0 : 1), isAdditiveTuning);
            var keyTracking = groupParams.GetValueOrDefault("keyTracking", "yes") == "yes";
            var reverse = groupParams.GetValueOrDefault("reverse") == "yes";
            var isOneShotGroup = ReadInternalModulators(groupElement, instrument, group.IsReleaseTrigger);
            ReadExternalModulators(groupElement, instrument, group.IsReleaseTrigger);

            var index = (string?)groupElement.Attribute("index");
            foreach (var zoneElement in zoneElements.Where(z => (string?)z.Attribute("groupIdx") == index))
            {
                var zone = ParseZone(zoneElement, instrument);
                if (zone is null)
                {
                    continue;
                }

                var zoneParams = Values(zoneElement.Element("Parameters"));
                zone.GainDb = Units.ValueToDb(GetDouble(zoneParams, "zoneVolume", 1) * groupVolume * programVolume);
                zone.Pan = Math.Clamp(GetDouble(zoneParams, "zonePan", 0) + groupPan + programPan, -1, 1);
                zone.Tuning = Units.RatioToSemitones(GetDouble(zoneParams, "zoneTune", 1) * groupTune * programTune);
                zone.KeyTracking = keyTracking;
                zone.Reverse = reverse;
                zone.OneShot |= isOneShotGroup;
                group.Zones.Add(zone);
            }

            if (group.Zones.Count > 0)
            {
                instrument.Groups.Add(group);
            }
        }

        instrument.VelocityToVolume ??= 0;
        return instrument;
    }

    private static SampleZone? ParseZone(XElement zoneElement, Instrument instrument)
    {
        var sampleValues = Values(zoneElement.Element("Sample"));
        var encoded = sampleValues.GetValueOrDefault("file_ex2") ?? sampleValues.GetValueOrDefault("file_ex") ?? sampleValues.GetValueOrDefault("file");
        if (string.IsNullOrEmpty(encoded))
        {
            instrument.Warnings.Add("A zone without sample file was skipped.");
            return null;
        }

        var p = Values(zoneElement.Element("Parameters"));
        var zone = new SampleZone
        {
            SamplePath = DecodeSamplePath(encoded),
            KeyRoot = Math.Clamp(GetInt(p, "rootKey", 60), 0, 127),
            KeyLow = Math.Clamp(GetInt(p, "lowKey", 0), 0, 127),
            KeyHigh = Math.Clamp(GetInt(p, "highKey", 127), 0, 127),
            VelocityLow = Math.Clamp(GetInt(p, "lowVelocity", 1), 1, 127),
            VelocityHigh = Math.Clamp(GetInt(p, "highVelocity", 127), 1, 127),
            KeyFadeLow = GetInt(p, "fadeLowKey", 0),
            KeyFadeHigh = GetInt(p, "fadeHighKey", 0),
            VelocityFadeLow = GetInt(p, "fadeLowVelo", 0),
            VelocityFadeHigh = GetInt(p, "fadeHighVelo", 0),
            SampleStart = Math.Max(0, GetInt(p, "sampleStart", 0))
        };

        // In the XML format the sample end is absolute, or negative = relative to the end of the sample
        var sampleEnd = GetInt(p, "sampleEnd", 0);
        if (sampleEnd < 0)
        {
            zone.SampleEndOffsetFromEnd = -sampleEnd;
        }
        else if (sampleEnd > zone.SampleStart)
        {
            zone.SampleEnd = sampleEnd;
        }

        foreach (var loopElement in zoneElement.Element("Loops")?.Elements("Loop") ?? [])
        {
            var lp = Values(loopElement);
            var mode = lp.GetValueOrDefault("mode") ?? string.Empty;
            if (mode == "oneshot")
            {
                zone.OneShot = true;
                continue;
            }

            if (mode is not ("until_end" or "until_release"))
            {
                continue;
            }

            var start = GetInt(lp, "loopStart", 0);
            var length = GetInt(lp, "loopLength", 0);
            if (length <= 0)
            {
                continue;
            }

            zone.Loops.Add(new SampleLoop
            {
                Start = start,
                End = start + length,
                Type = lp.GetValueOrDefault("alternatingLoop") == "yes" ? LoopType.Alternating : LoopType.Forward,
                UntilRelease = mode == "until_release",
                CrossfadeSamples = Math.Max(0, GetInt(lp, "xfadeLength", 0)),
                Tuning = Units.RatioToSemitones(GetDouble(lp, "loopTuning", 1))
            });
        }

        return zone;
    }

    /// <returns>True if the volume envelope is in "note-off less" (one-shot) mode.</returns>
    private static bool ReadInternalModulators(XElement groupElement, Instrument instrument, bool isReleaseGroup)
    {
        var oneShot = false;
        foreach (var modulator in groupElement.Element("IntModulators")?.Elements("K2_IntMod") ?? [])
        {
            var envElement = modulator.Element("Envelope");
            if (envElement is null || Values(modulator).GetValueOrDefault("bypass") == "yes")
            {
                continue;
            }

            var targets = (modulator.Element("Targets")?.Elements("Target") ?? []).Select(Values).ToList();
            var volumeTarget = targets.FirstOrDefault(t => t.GetValueOrDefault("target") == "volume");
            if (volumeTarget is null || GetDouble(volumeTarget, "intensity", 0) == 0)
            {
                continue;
            }

            var env = Values(envElement);
            if (env.GetValueOrDefault("noteOffLessMode") == "yes")
            {
                oneShot = true;
            }

            // Ableton Sampler has a single amp envelope: use the first one of a normal (non release-trigger) group
            if (isReleaseGroup || instrument.AmpEnvelope is not null)
            {
                continue;
            }

            var type = (string?)envElement.Attribute("type");
            Envelope? envelope = type switch
            {
                "ahdsr" or "ahd" => new Envelope
                {
                    AttackSlope = -GetDouble(env, "atkCurving", 0),
                    Attack = GetDouble(env, "attack", 0) / 1000.0,
                    Hold = GetDouble(env, "hold", 0) / 1000.0,
                    Decay = GetDouble(env, "decay", 0) / 1000.0,
                    Sustain = type == "ahd" ? 0 : Math.Clamp(GetDouble(env, "sustain", 1), 0, 1),
                    Release = (type == "ahd" ? GetDouble(env, "decay", 0) : GetDouble(env, "release", 0)) / 1000.0
                },
                "dbd" => new Envelope
                {
                    Decay = (GetDouble(env, "decay1", 0) + (GetDouble(env, "break", 0) == 0 ? 0 : GetDouble(env, "decay2", 0))) / 1000.0,
                    Sustain = 0
                },
                _ => null
            };

            if (envelope is not null)
            {
                instrument.AmpEnvelope = envelope;
            }
        }

        return oneShot;
    }

    private static void ReadExternalModulators(XElement groupElement, Instrument instrument, bool isReleaseGroup)
    {
        if (isReleaseGroup)
        {
            return;
        }

        foreach (var modulator in groupElement.Element("ExtModulators")?.Elements("K2_ExtMod") ?? [])
        {
            var parameters = Values(modulator);
            if (parameters.GetValueOrDefault("bypass") == "yes")
            {
                continue;
            }

            var source = parameters.GetValueOrDefault("source");
            var targets = parameters.ContainsKey("target")
                ? [parameters]
                : (modulator.Element("Targets")?.Elements("Target") ?? []).Select(Values).ToList();

            foreach (var target in targets)
            {
                var name = target.GetValueOrDefault("target");
                var intensity = GetDouble(target, "intensity", double.NaN);
                if (double.IsNaN(intensity))
                {
                    continue;
                }

                if (source == "pitchBend" && name == "pitch")
                {
                    instrument.PitchBendRange = Math.Clamp((int)Math.Round(Math.Abs(intensity) * 12), 0, 48);
                }
                else if (source == "velocity" && name == "volume" && instrument.VelocityToVolume is null)
                {
                    instrument.VelocityToVolume = Math.Clamp(intensity, 0, 1);
                }
            }
        }
    }

    private static double ToTuneRatio(double tune, bool additive) => additive ? Math.Pow(2.0, tune) : tune;

    private static Dictionary<string, string> Values(XElement? element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element is null)
        {
            return result;
        }

        foreach (var value in element.Elements("V"))
        {
            var name = (string?)value.Attribute("name");
            var content = (string?)value.Attribute("value");
            if (name is not null && content is not null)
            {
                result[name] = content;
            }
        }

        return result;
    }

    private static double GetDouble(Dictionary<string, string> values, string name, double defaultValue) =>
        values.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;

    private static int GetInt(Dictionary<string, string> values, string name, int defaultValue)
    {
        if (!values.TryGetValue(name, out var text))
        {
            return defaultValue;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : defaultValue;
    }

    /// <summary>
    /// Decodes Kontakt's encoded sample path, e.g. "@b001d007Samplesf123456piano.wav":
    /// 'v' = volume, 'b' = parent folder, 'd' = folder, 'm' = library (container), 'f'/'F' = file name follows.
    /// Folder names are prefixed with their 3-digit length.
    /// </summary>
    internal static string DecodeSamplePath(string encoded)
    {
        if (encoded.Length == 0 || encoded[0] != '@')
        {
            return encoded.Replace('\\', '/');
        }

        var path = new StringBuilder();
        var pos = 1;
        while (pos < encoded.Length)
        {
            var id = encoded[pos++];
            switch (id)
            {
                case 'v':
                    var volume = ReadFolder(encoded, ref pos);
                    if (!volume.Contains(':'))
                    {
                        path.Append('/');
                    }

                    path.Append(volume).Append('/');
                    break;
                case 'b':
                    path.Append("../");
                    break;
                case 'd':
                    path.Append(ReadFolder(encoded, ref pos)).Append('/');
                    break;
                case 'm':
                    // Samples inside a library container (NKX/NCW monolith) - only the path inside the library is useful
                    ReadFolder(encoded, ref pos);
                    path.Clear();
                    break;
                case 'f':
                case 'F':
                    pos += id == 'f' ? 6 : 11;
                    if (pos > encoded.Length)
                    {
                        throw new InvalidDataException($"Malformed sample path: {encoded}");
                    }

                    path.Append(encoded, pos, encoded.Length - pos);
                    return DecodeUtf(path.ToString());
                default:
                    throw new InvalidDataException($"Malformed sample path: {encoded}");
            }
        }

        throw new InvalidDataException($"Malformed sample path: {encoded}");
    }

    private static string ReadFolder(string encoded, ref int pos)
    {
        if (pos + 3 > encoded.Length || !int.TryParse(encoded.AsSpan(pos, 3), out var length) || pos + 3 + length > encoded.Length)
        {
            throw new InvalidDataException($"Malformed sample path: {encoded}");
        }

        var folder = encoded.Substring(pos + 3, length);
        pos += 3 + length;
        return folder;
    }

    private static string DecodeUtf(string text) =>
        text.Contains('%')
            ? UtfEscape().Replace(text, m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value, NumberStyles.HexNumber)))
            : text;

    [GeneratedRegex("%([0-9a-fA-F]{4})")]
    private static partial Regex UtfEscape();
}
