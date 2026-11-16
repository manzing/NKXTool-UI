using NkiConverter.IO;

namespace NkiConverter.Kontakt;

/// <summary>A Kontakt program (instrument) as stored in the binary preset data of Kontakt 4.2 and later.</summary>
internal sealed class K5Program
{
    private const int VoiceGroupVersion = 0x60;
    private const int LoopVersion = 0x60;

    public string Name { get; private set; } = string.Empty;
    public int MidiTranspose { get; private set; }
    public float Volume { get; private set; } = 1;
    public float Pan { get; private set; }
    public float Tune { get; private set; } = 1;
    public bool GroupSolo { get; private set; }
    public string Credits { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;
    public string Url { get; private set; } = string.Empty;
    public int MaxVoices { get; private set; }

    public List<K5Group> Groups { get; } = [];
    public List<K5Zone> Zones { get; } = [];
    public List<string> Warnings { get; } = [];

    public static K5Program Read(PresetChunk chunk)
    {
        var program = new K5Program();
        program.ReadProgramData(chunk.PublicData);

        foreach (var child in chunk.Children)
        {
            switch (child.Id)
            {
                case PresetChunkId.GroupList:
                    foreach (var groupChunk in child.Children)
                    {
                        program.Groups.Add(K5Group.Read(groupChunk, program.Warnings));
                    }

                    break;

                case PresetChunkId.ZoneList:
                    foreach (var zoneChunk in child.Children)
                    {
                        var zone = K5Zone.Read(zoneChunk.Id, zoneChunk.PublicData, zoneChunk.Version, program.Warnings);
                        foreach (var zoneChild in zoneChunk.Children)
                        {
                            if (zoneChild.Id == PresetChunkId.LoopArray)
                            {
                                ReadLoops(zone, zoneChild.PublicData, program.Warnings);
                            }
                        }

                        program.Zones.Add(zone);
                    }

                    break;

                case PresetChunkId.VoiceGroups:
                    program.ReadVoiceGroups(child.PublicData);
                    break;
            }
        }

        return program;
    }

    private void ReadProgramData(byte[] data)
    {
        var r = new ByteReader(data);
        Name = r.Utf16WithLength();
        r.F64(); // size of all samples
        MidiTranspose = unchecked((sbyte)r.U8());
        Volume = r.F32();
        Pan = r.F32();
        Tune = r.F32();
        r.U8(); // clip low velocity
        r.U8(); // clip high velocity
        r.U8(); // clip low key
        r.U8(); // clip high key
        r.U16(); // default key switch
        r.U32(); // DFD channel preload size
        r.U32(); // library ID
        r.U32(); // fingerprint
        r.U32(); // loading flags
        GroupSolo = r.U8() > 0;
        r.U32(); // icon
        Credits = Clean(r.Utf16WithLength());
        Author = Clean(r.Utf16WithLength().Replace('￼', 'ü'));
        Url = Clean(r.Utf16WithLength());
    }

    private static string Clean(string value)
    {
        var trimmed = value.Trim();
        return trimmed == "(null)" ? string.Empty : trimmed;
    }

    private void ReadVoiceGroups(byte[] data)
    {
        try
        {
            var r = new ByteReader(data);
            if (r.U16() != VoiceGroupVersion)
            {
                return;
            }

            r.Utf16WithLength();
            r.Skip(3);
            var voices = r.U32();
            if (voices is > 0 and <= 1000)
            {
                MaxVoices = (int)voices;
            }
        }
        catch (InvalidDataException)
        {
            // optional information
        }
    }

    private static void ReadLoops(K5Zone zone, byte[] data, List<string> warnings)
    {
        if (data.Length < 2)
        {
            return;
        }

        try
        {
            var r = new ByteReader(data);
            var enabled = r.U16();
            for (var i = 0; i < 8; i++)
            {
                if ((enabled & (1 << i)) == 0)
                {
                    continue;
                }

                var version = r.U16();
                if (version != LoopVersion)
                {
                    warnings.Add($"Unknown loop format 0x{version:X} - loops of a zone were skipped.");
                    return;
                }

                var loop = new K5Loop
                {
                    Mode = r.I32(),
                    Start = (int)r.U32(),
                    Length = (int)r.U32(),
                    Count = (int)r.U32(),
                    Alternating = r.U8() > 0,
                    Tuning = r.F32(),
                    CrossfadeLength = (int)r.U32()
                };
                zone.Loops.Add(loop);

                if (r.HasMore)
                {
                    r.Skip(1);
                }
            }
        }
        catch (InvalidDataException)
        {
            warnings.Add("Could not read the loops of a zone.");
        }
    }
}

internal sealed class K5Loop
{
    public const int ModeUntilEnd = 1;
    public const int ModeUntilRelease = 2;
    public const int ModeOneShot = 3;

    public int Mode { get; init; }
    public int Start { get; init; }
    public int Length { get; init; }
    public int Count { get; init; }
    public bool Alternating { get; init; }
    public float Tuning { get; init; } = 1;
    public int CrossfadeLength { get; init; }
}

internal sealed class K5Zone
{
    public int GroupIndex { get; init; }
    public int SampleStart { get; private set; }
    public int SampleEndOffset { get; private set; }
    public int LowVelocity { get; private set; } = 1;
    public int HighVelocity { get; private set; } = 127;
    public int LowKey { get; private set; }
    public int HighKey { get; private set; } = 127;
    public int FadeLowVelocity { get; private set; }
    public int FadeHighVelocity { get; private set; }
    public int FadeLowKey { get; private set; }
    public int FadeHighKey { get; private set; }
    public int RootKey { get; private set; } = 60;
    public float Volume { get; private set; } = 1;
    public float Pan { get; private set; }
    public float Tune { get; private set; } = 1;
    public int FilenameId { get; private set; } = -1;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int NumFrames { get; private set; }
    public List<K5Loop> Loops { get; } = [];

    public static K5Zone Read(int groupIndex, byte[] data, int version, List<string> warnings)
    {
        var zone = new K5Zone { GroupIndex = groupIndex };
        if (version > 0x9C)
        {
            warnings.Add($"Zone format version 0x{version:X} is newer than tested (0x9C); reading with the latest known layout.");
        }

        var r = new ByteReader(data);
        zone.SampleStart = (int)r.U32();
        zone.SampleEndOffset = (int)r.U32();
        r.U32(); // sample start modulation range
        zone.LowVelocity = r.U16();
        zone.HighVelocity = r.U16();
        zone.LowKey = r.U16();
        zone.HighKey = r.U16();
        zone.FadeLowVelocity = r.U16();
        zone.FadeHighVelocity = r.U16();
        zone.FadeLowKey = r.U16();
        zone.FadeHighKey = r.U16();
        zone.RootKey = r.U16();
        zone.Volume = r.F32();
        zone.Pan = r.F32();
        zone.Tune = r.F32();

        if (version >= 0x9A)
        {
            r.U8();
            r.U8();
            r.U32();
            if (!r.HasMore)
            {
                return zone;
            }
        }

        zone.FilenameId = r.I32();
        r.U32(); // sample resolution (bytes)
        zone.SampleRate = (int)r.U32();
        zone.Channels = r.U8();
        zone.NumFrames = (int)r.U32();
        // The remaining values (root note / tuning of the sample file, sample size) are not needed
        return zone;
    }
}

internal sealed class K5Group
{
    public const int StartModeAlways = 0;
    public const int StartModeCycleRoundRobin = 3;
    public const int StartModeCycleRandom = 4;
    private const int StartCriteriaVersion = 0x70;
    private const int StartCriteriaSize = 34;

    public string Name { get; private set; } = string.Empty;
    public float Volume { get; private set; } = 1;
    public float Pan { get; private set; }
    public float Tune { get; private set; } = 1;
    public bool KeyTracking { get; private set; } = true;
    public bool Reverse { get; private set; }
    public bool ReleaseTrigger { get; private set; }
    public int VoiceGroupIndex { get; private set; } = -1;
    public bool Muted { get; private set; }
    public bool Soloed { get; private set; }
    public int StartCriteriaMode { get; private set; } = StartModeAlways;
    public int CycleClass { get; private set; } = -1;
    public List<K5InternalModulator> InternalModulators { get; } = [];
    public List<K5ExternalModulator> ExternalModulators { get; } = [];

    public static K5Group Read(PresetChunk chunk, List<string> warnings)
    {
        var group = new K5Group();
        if (chunk.Version > 0x9C)
        {
            warnings.Add($"Group format version 0x{chunk.Version:X} is newer than tested (0x9C); reading with the latest known layout.");
        }

        var r = new ByteReader(chunk.PublicData);
        group.Name = r.Utf16WithLength();
        group.Volume = r.F32();
        group.Pan = r.F32();
        group.Tune = r.F32();
        group.KeyTracking = r.U8() > 0;
        group.Reverse = r.U8() > 0;
        group.ReleaseTrigger = r.U8() > 0;
        r.U8(); // release trigger note monophonic
        r.I32(); // release trigger counter
        r.I16(); // MIDI channel
        group.VoiceGroupIndex = r.I32();
        r.I32(); // FX index amp split point
        group.Muted = r.U8() > 0;
        group.Soloed = r.U8() > 0;

        foreach (var child in chunk.Children)
        {
            switch (child.Id)
            {
                case PresetChunkId.ParameterArray16:
                    foreach (var mod in child.Children)
                    {
                        if (mod.Id is PresetChunkId.ParInternalMod or PresetChunkId.ParModBase)
                        {
                            var internalModulator = K5InternalModulator.TryRead(mod);
                            if (internalModulator is not null)
                            {
                                group.InternalModulators.Add(internalModulator);
                            }
                        }
                    }

                    break;

                case PresetChunkId.ParameterArray32:
                    foreach (var mod in child.Children)
                    {
                        if (mod.Id == PresetChunkId.ParExternalMod)
                        {
                            var externalModulator = K5ExternalModulator.TryRead(mod.PublicData);
                            if (externalModulator is not null)
                            {
                                group.ExternalModulators.Add(externalModulator);
                            }
                        }
                    }

                    break;

                case PresetChunkId.StartCriteriaList:
                    group.ReadStartCriteria(child.PublicData);
                    break;
            }
        }

        return group;
    }

    private void ReadStartCriteria(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var count = data[0];
        for (var i = 0; i < count; i++)
        {
            var offset = 1 + i * StartCriteriaSize;
            if (offset + StartCriteriaSize > data.Length || ByteReader.U16At(data, offset + 1) != StartCriteriaVersion)
            {
                return;
            }

            var mode = (int)ByteReader.U32At(data, offset + 3);
            if (mode is StartModeCycleRoundRobin or StartModeCycleRandom)
            {
                StartCriteriaMode = mode;
                CycleClass = ByteReader.U16At(data, offset + 21);
                return;
            }
        }
    }
}

/// <summary>Kontakt modulation shapers which may follow a modulation target.</summary>
internal static class ModulationShaper
{
    private const int TableSteps = 128;
    private const int TypeTable = 1;
    private const int TypeCurve = 2;

    public static int Skip(byte[] data, int offset)
    {
        if (offset >= data.Length)
        {
            return -1;
        }

        return data[offset] switch
        {
            0 => offset + 1,
            TypeTable => offset + 2 + TableSteps * 4,
            TypeCurve => offset + 3 > data.Length ? -1 : offset + 3 + data[offset + 2] * 3 * 4,
            _ => -1
        };
    }
}

/// <summary>An internal modulator of a group (envelope or LFO). Only envelopes are evaluated.</summary>
internal sealed class K5InternalModulator
{
    private const int FlagInvert = 0x02;
    private const int FlagNoDescription = 0x08;
    private const int SourceEnvelope = 0;
    private const int SourceLfo = 1;
    private const int SourceEnvelope2 = 2;

    private readonly List<(string Name, float Intensity)> _targets = [];
    private bool _bypassed;
    private string _sourceName = "ENV_AHDSR";
    private int _sourceIndex;
    private float _curve = -0.332f;
    private float _attack;
    private float _hold;
    private float _decay = 500;
    private float _release = 300;
    private float _sustain = 1;
    private bool _ahdOnly;
    private bool _decayBreakDecay;
    private bool _hasEnvelopeData;

    public static K5InternalModulator? TryRead(PresetChunk chunk)
    {
        try
        {
            var modulator = new K5InternalModulator();
            return modulator.Read(chunk) ? modulator : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private bool Read(PresetChunk chunk)
    {
        var r = new ByteReader(chunk.PublicData);
        if (chunk.Id == PresetChunkId.ParModBase)
        {
            r.U32();
        }

        int mainVersion = r.U8();
        if (mainVersion == 0)
        {
            mainVersion = r.U8();
        }

        if (mainVersion != 1)
        {
            return false;
        }

        var version = r.U16();
        if (version is < 0x80 or > 0x81)
        {
            return false;
        }

        var block1 = r.Bytes((int)r.U32());
        if (block1.Length < 4)
        {
            return false;
        }

        var count = (int)ByteReader.U32At(block1, 0);
        if (!ReadTargets(block1, 4, count))
        {
            return false;
        }

        if (r.U32() != 0)
        {
            return false;
        }

        var block2 = r.Bytes((int)r.U32());
        ReadBlock2(block2);
        return true;
    }

    private bool ReadTargets(byte[] data, int offset, int remaining)
    {
        if (remaining == 0)
        {
            return ReadTrailer(data, offset) || ReadTrailer(data, offset + 1);
        }

        var name = ByteReader.AsciiWithLengthAt(data, offset);
        if (name is null)
        {
            return false;
        }

        var pos = offset + 4 + name.Length;
        if (pos + 9 > data.Length)
        {
            return false;
        }

        var intensity = ByteReader.F32At(data, pos);
        if (ByteReader.U16At(data, pos + 4) != 0xFFFF)
        {
            return false;
        }

        var flags = data[pos + 6];
        pos += 9;
        _targets.Add((name, (flags & FlagInvert) > 0 ? -intensity : intensity));

        if ((flags & FlagNoDescription) > 0)
        {
            if (ReadTargets(data, pos + 5, remaining - 1))
            {
                return true;
            }
        }
        else
        {
            var description = ByteReader.AsciiWithLengthAt(data, pos);
            if (description is not null)
            {
                pos += 4 + description.Length;
                for (var unknown = 0; unknown <= 2; unknown++)
                {
                    var next = ModulationShaper.Skip(data, pos + unknown);
                    if (next >= 0 && ReadTargets(data, next, remaining - 1))
                    {
                        return true;
                    }
                }
            }
        }

        _targets.RemoveAt(_targets.Count - 1);
        return false;
    }

    private bool ReadTrailer(byte[] data, int offset)
    {
        if (offset + 8 > data.Length)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (data[offset + i] > 1)
            {
                return false;
            }
        }

        var sourceName = ByteReader.AsciiWithLengthAt(data, offset + 8);
        if (sourceName is null)
        {
            return false;
        }

        var pos = offset + 12 + sourceName.Length;
        if (pos + 4 != data.Length)
        {
            return false;
        }

        _bypassed = data[offset + 1] > 0;
        _sourceName = sourceName;
        _sourceIndex = (int)ByteReader.U32At(data, pos);
        return true;
    }

    private bool IsEnvelope => _sourceIndex is SourceEnvelope or SourceEnvelope2;

    private void ReadBlock2(byte[] data)
    {
        if (!IsEnvelope || _sourceIndex == SourceLfo)
        {
            return;
        }

        if (_sourceName == "ENV_DBD")
        {
            if (data.Length < 46)
            {
                return;
            }

            var decay1 = ByteReader.F32At(data, 34);
            var breakLevel = ByteReader.F32At(data, 38);
            var decay2 = ByteReader.F32At(data, 42);
            _decay = breakLevel == 0 ? decay1 : decay1 + decay2;
            _decayBreakDecay = true;
            _hasEnvelopeData = true;
            return;
        }

        if (_sourceName is "ENV_AHDSR" or "ENV_AHD" or "<none>" or "")
        {
            if (data.Length < 59)
            {
                return;
            }

            _curve = ByteReader.F32At(data, 34);
            _attack = ByteReader.F32At(data, 38);
            _decay = ByteReader.F32At(data, 42);
            _hold = ByteReader.F32At(data, 46);
            _release = ByteReader.F32At(data, 50);
            _sustain = ByteReader.F32At(data, 54);
            _ahdOnly = data[58] > 0 || _sourceName == "ENV_AHD";
            _hasEnvelopeData = true;
        }
    }

    /// <summary>Returns the envelope if this modulator is an (active) envelope which modulates the given target.</summary>
    public Model.Envelope? GetEnvelope(string target, out double intensity)
    {
        intensity = 0;
        if (_bypassed || !IsEnvelope || !_hasEnvelopeData)
        {
            return null;
        }

        var index = _targets.FindIndex(t => t.Name == target);
        if (index < 0)
        {
            return null;
        }

        intensity = _targets[index].Intensity;
        if (_decayBreakDecay)
        {
            return new Model.Envelope { Decay = _decay / 1000.0, Sustain = 0 };
        }

        return new Model.Envelope
        {
            AttackSlope = -_curve,
            Attack = Math.Max(0, _attack) / 1000.0,
            Hold = Math.Max(0, _hold) / 1000.0,
            Decay = Math.Max(0, _decay) / 1000.0,
            Sustain = _ahdOnly ? 0 : Math.Clamp(_sustain, 0, 1),
            Release = Math.Max(0, _ahdOnly ? _decay : _release) / 1000.0
        };
    }
}

/// <summary>An external modulator of a group (e.g. velocity -> volume, pitch bend -> pitch).</summary>
internal sealed class K5ExternalModulator
{
    public const int SourceVelocity = 0x00;
    public const int SourcePitchBend = 0x01;
    public const int SourceVelocity2 = 0x06;
    private const int FlagInvert = 0x02;

    public string TargetName { get; private set; } = string.Empty;
    public long SourceType { get; private set; } = -1;
    public float Intensity { get; private set; } = 1;

    public static K5ExternalModulator? TryRead(byte[] data)
    {
        try
        {
            if (data.Length < 11)
            {
                return null;
            }

            var size = ByteReader.U32At(data, 3);
            if (size < 4 || size > data.Length - 7L)
            {
                return null;
            }

            var block = data.AsSpan(7, (int)size).ToArray();
            var count = ByteReader.U32At(block, 0);
            var modulator = new K5ExternalModulator();
            if (count == 0 || !modulator.ReadParameters(block, 4, (int)Math.Min(count, 64), true))
            {
                return null;
            }

            return modulator;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private bool ReadParameters(byte[] data, int offset, int remaining, bool isFirst)
    {
        if (remaining == 0)
        {
            return ReadSource(data, offset) || ReadSource(data, offset + 1);
        }

        var name = ByteReader.AsciiWithLengthAt(data, offset);
        if (name is null)
        {
            return false;
        }

        var pos = offset + 4 + name.Length;
        if (pos + 9 > data.Length || ByteReader.U16At(data, pos + 4) != 0xFFFF)
        {
            return false;
        }

        var intensity = ByteReader.F32At(data, pos);
        var flags = data[pos + 6];
        pos += 9;
        var description = ByteReader.AsciiWithLengthAt(data, pos);
        if (description is null)
        {
            return false;
        }

        pos += 4 + description.Length;
        for (var unknown = 0; unknown <= 2; unknown++)
        {
            var next = ModulationShaper.Skip(data, pos + unknown);
            if (next >= 0 && ReadParameters(data, next, remaining - 1, false))
            {
                if (isFirst)
                {
                    TargetName = name;
                    Intensity = (flags & FlagInvert) > 0 ? -intensity : intensity;
                }

                return true;
            }
        }

        return false;
    }

    private bool ReadSource(byte[] data, int offset)
    {
        var name = ByteReader.AsciiWithLengthAt(data, offset);
        if (name is null)
        {
            return false;
        }

        var pos = offset + 4 + name.Length;
        if (pos + 8 > data.Length || ByteReader.U32At(data, pos) != 1)
        {
            return false;
        }

        SourceType = ByteReader.U32At(data, pos + 4);
        return true;
    }
}
