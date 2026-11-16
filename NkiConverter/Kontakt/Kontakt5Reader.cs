using NkiConverter.IO;
using NkiConverter.Model;

namespace NkiConverter.Kontakt;

/// <summary>Converts the binary preset data of Kontakt 4.2 and later into instruments.</summary>
internal static class Kontakt5Reader
{
    /// <summary>Reads a Kontakt 5+ NKI (NI container format, starts with a 'hsin' item).</summary>
    public static List<Instrument> ReadContainer(byte[] fileData, string sourceFile)
    {
        var root = NiContainerItem.Read(new ByteReader(fileData));

        var presetChunk = root.Find(NiChunkType.PresetChunkItem);
        if (presetChunk is null)
        {
            if (root.HasEncryptedSubTree() || root.Find(NiChunkType.Authorization) is not null)
            {
                throw new NkiEncryptedException(
                    "The instrument data is encrypted (licensed Kontakt Player library). Encrypted NKI files cannot be converted.");
            }

            throw new InvalidDataException("The NKI file does not contain Kontakt preset data.");
        }

        var presetData = ReadPresetChunkData(presetChunk.Data);
        var instruments = ReadPresetData(presetData, sourceFile, "Kontakt 5+");

        var soundInfoChunk = root.Find(NiChunkType.SoundinfoItem);
        var soundInfo = soundInfoChunk is null ? null : SoundInfo.TryParse(soundInfoChunk.Data);
        if (soundInfo is not null)
        {
            foreach (var instrument in instruments)
            {
                ApplySoundInfo(instrument, soundInfo);
            }
        }

        return instruments;
    }

    private static byte[] ReadPresetChunkData(byte[] data)
    {
        var r = new ByteReader(data);
        r.U32(); // version
        r.U32(); // dictionary type
        var items = r.U32();
        if (items != 1)
        {
            throw new InvalidDataException($"Unexpected number of preset items ({items}).");
        }

        var size = (int)r.U32();
        r.U32();
        return r.Bytes(size);
    }

    public static List<Instrument> ReadPresetData(byte[] presetData, string sourceFile, string formatName)
    {
        var chunks = PresetChunk.ReadAll(presetData);
        var programChunks = PresetChunk.FindAllRecursive(chunks, PresetChunkId.Program).ToList();
        if (programChunks.Count == 0)
        {
            throw new InvalidDataException("No Kontakt program found in the preset data.");
        }

        var filePaths = FileList.ReadSampleFiles(chunks);
        var instruments = new List<Instrument>();
        foreach (var programChunk in programChunks)
        {
            var program = K5Program.Read(programChunk);
            instruments.Add(Convert(program, filePaths, sourceFile, formatName));
        }

        return instruments;
    }

    private static Instrument Convert(K5Program program, List<string> filePaths, string sourceFile, string formatName)
    {
        var instrument = new Instrument
        {
            Name = string.IsNullOrWhiteSpace(program.Name) ? Path.GetFileNameWithoutExtension(sourceFile) : program.Name.Trim(),
            SourceFile = sourceFile,
            SourceFormat = formatName,
            MaxVoices = program.MaxVoices
        };
        instrument.Warnings.AddRange(program.Warnings);
        instrument.Metadata.Author = program.Author;
        instrument.Metadata.Description = program.Credits;
        instrument.Metadata.Url = program.Url;

        var hasSoloedGroup = program.GroupSolo && program.Groups.Any(g => g.Soloed);
        var groups = new Dictionary<int, SampleGroup>();

        foreach (var kontaktZone in program.Zones)
        {
            if (kontaktZone.FilenameId < 0)
            {
                continue;
            }

            if (kontaktZone.GroupIndex < 0 || kontaktZone.GroupIndex >= program.Groups.Count)
            {
                instrument.Warnings.Add($"A zone references the missing group {kontaktZone.GroupIndex} and was skipped.");
                continue;
            }

            var kontaktGroup = program.Groups[kontaktZone.GroupIndex];
            if (hasSoloedGroup ? !kontaktGroup.Soloed : kontaktGroup.Muted)
            {
                continue;
            }

            if (kontaktZone.FilenameId >= filePaths.Count)
            {
                instrument.Warnings.Add($"A zone references the unknown sample #{kontaktZone.FilenameId} and was skipped.");
                continue;
            }

            if (!groups.TryGetValue(kontaktZone.GroupIndex, out var group))
            {
                group = new SampleGroup
                {
                    Name = kontaktGroup.Name,
                    IsReleaseTrigger = kontaktGroup.ReleaseTrigger
                };

                if (kontaktGroup.StartCriteriaMode is K5Group.StartModeCycleRoundRobin or K5Group.StartModeCycleRandom)
                {
                    group.PlayLogic = kontaktGroup.StartCriteriaMode == K5Group.StartModeCycleRandom ? PlayLogic.Random : PlayLogic.RoundRobin;
                    group.RoundRobinPosition = kontaktGroup.CycleClass >= 0 ? kontaktGroup.CycleClass + 1 : 0;
                }

                groups[kontaktZone.GroupIndex] = group;
            }

            var zone = new SampleZone
            {
                SamplePath = filePaths[kontaktZone.FilenameId],
                KeyLow = Math.Clamp(kontaktZone.LowKey, 0, 127),
                KeyHigh = Math.Clamp(kontaktZone.HighKey, 0, 127),
                KeyRoot = Math.Clamp(kontaktZone.RootKey, 0, 127),
                VelocityLow = Math.Clamp(kontaktZone.LowVelocity, 1, 127),
                VelocityHigh = Math.Clamp(kontaktZone.HighVelocity, 1, 127),
                KeyFadeLow = kontaktZone.FadeLowKey,
                KeyFadeHigh = kontaktZone.FadeHighKey,
                VelocityFadeLow = kontaktZone.FadeLowVelocity,
                VelocityFadeHigh = kontaktZone.FadeHighVelocity,
                SampleStart = Math.Max(0, kontaktZone.SampleStart),
                SampleEndOffsetFromEnd = Math.Max(0, kontaktZone.SampleEndOffset),
                StoredFrames = kontaktZone.NumFrames,
                GainDb = Units.ValueToDb(program.Volume * kontaktGroup.Volume * kontaktZone.Volume),
                Pan = Math.Clamp(program.Pan + kontaktGroup.Pan + kontaktZone.Pan, -1, 1),
                Tuning = Units.RatioToSemitones(kontaktZone.Tune * kontaktGroup.Tune * program.Tune),
                KeyTracking = kontaktGroup.KeyTracking,
                Reverse = kontaktGroup.Reverse
            };

            foreach (var loop in kontaktZone.Loops)
            {
                if (loop.Mode == K5Loop.ModeOneShot)
                {
                    zone.OneShot = true;
                }
                else if ((loop.Mode is K5Loop.ModeUntilEnd or K5Loop.ModeUntilRelease) && loop.Length > 0)
                {
                    zone.Loops.Add(new SampleLoop
                    {
                        Start = loop.Start,
                        End = loop.Start + loop.Length,
                        Type = loop.Alternating ? LoopType.Alternating : LoopType.Forward,
                        UntilRelease = loop.Mode == K5Loop.ModeUntilRelease,
                        CrossfadeSamples = Math.Max(0, loop.CrossfadeLength),
                        Tuning = Units.RatioToSemitones(loop.Tuning)
                    });
                }
            }

            group.Zones.Add(zone);
        }

        // Keep the group order of the instrument
        foreach (var index in groups.Keys.Order())
        {
            instrument.Groups.Add(groups[index]);
        }

        ApplyGroupModulation(instrument, program, groups);
        return instrument;
    }

    private static void ApplyGroupModulation(Instrument instrument, K5Program program, Dictionary<int, SampleGroup> usedGroups)
    {
        // Ableton Sampler has one global envelope / pitch bend / velocity setting. Take them from the first
        // used, non release-triggered group which defines them.
        var candidates = usedGroups
            .OrderBy(pair => pair.Value.IsReleaseTrigger ? 1 : 0)
            .ThenBy(pair => pair.Key)
            .Select(pair => program.Groups[pair.Key])
            .ToList();

        foreach (var group in candidates)
        {
            foreach (var modulator in group.InternalModulators)
            {
                var envelope = modulator.GetEnvelope("volume", out _);
                if (envelope is not null)
                {
                    instrument.AmpEnvelope = envelope;
                    break;
                }
            }

            if (instrument.AmpEnvelope is not null)
            {
                break;
            }
        }

        foreach (var group in candidates)
        {
            var bend = group.ExternalModulators.FirstOrDefault(m => m.TargetName == "pitch" && m.SourceType == K5ExternalModulator.SourcePitchBend);
            if (bend is not null)
            {
                instrument.PitchBendRange = Math.Clamp((int)Math.Round(Math.Abs(bend.Intensity) * 12), 0, 48);
                break;
            }
        }

        foreach (var group in candidates)
        {
            var velocity = group.ExternalModulators.FirstOrDefault(m =>
                m.TargetName == "volume" && m.SourceType is K5ExternalModulator.SourceVelocity or K5ExternalModulator.SourceVelocity2);
            if (velocity is not null)
            {
                instrument.VelocityToVolume = Math.Clamp(velocity.Intensity, 0, 1);
                break;
            }
        }

        instrument.VelocityToVolume ??= candidates.Count > 0 ? 0 : null;
    }

    private static void ApplySoundInfo(Instrument instrument, SoundInfo info)
    {
        static bool IsSet(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim() != "-";

        if (string.IsNullOrWhiteSpace(instrument.Metadata.Author) && IsSet(info.Author))
        {
            instrument.Metadata.Author = info.Author.Trim();
        }

        if (IsSet(info.Vendor))
        {
            instrument.Metadata.Vendor = info.Vendor.Trim();
        }

        if (IsSet(info.Description) && !instrument.Metadata.Description.Contains(info.Description.Trim(), StringComparison.Ordinal))
        {
            instrument.Metadata.Description = string.IsNullOrWhiteSpace(instrument.Metadata.Description)
                ? info.Description.Trim()
                : instrument.Metadata.Description + "\n" + info.Description.Trim();
        }

        if (info.Properties.TryGetValue("library", out var library) && IsSet(library))
        {
            instrument.Metadata.Library = library;
        }

        foreach (var tag in info.Attributes.Concat(info.Tags))
        {
            if (IsSet(tag) && tag != "Kontakt" && !instrument.Metadata.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                instrument.Metadata.Tags.Add(tag.Trim());
            }
        }

        if (string.IsNullOrEmpty(instrument.Metadata.Category) && info.Attributes.Count > 0)
        {
            instrument.Metadata.Category = info.Attributes[0];
        }
    }
}

/// <summary>Thrown when the NKI is encrypted and therefore cannot be converted.</summary>
public sealed class NkiEncryptedException(string message) : Exception(message);

internal static class Units
{
    private const double MinimumDb = -150;

    /// <summary>Linear gain to dB, using 6 dB per doubling like Kontakt.</summary>
    public static double ValueToDb(double value) => value <= 0 ? MinimumDb : Math.Max(MinimumDb, 6.0 * Math.Log2(value));

    public static double DbToValue(double db) => Math.Pow(2, db / 6.0);

    public static double RatioToSemitones(double ratio)
    {
        if (ratio <= 0 || double.IsNaN(ratio))
        {
            return 0;
        }

        return Math.Round(12.0 * Math.Log2(ratio) * 100000) / 100000.0;
    }
}
