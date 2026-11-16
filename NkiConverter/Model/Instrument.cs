namespace NkiConverter.Model;

/// <summary>Format neutral description of a multi-sampled instrument read from a Kontakt NKI.</summary>
public sealed class Instrument
{
    public string Name { get; set; } = string.Empty;

    public string SourceFile { get; set; } = string.Empty;

    /// <summary>Version / flavour of the source file, e.g. "Kontakt 5+ container".</summary>
    public string SourceFormat { get; set; } = string.Empty;

    public InstrumentMetadata Metadata { get; } = new();

    public List<SampleGroup> Groups { get; } = [];

    /// <summary>Amplitude envelope (Kontakt applies it per group; Ableton Sampler has one global envelope).</summary>
    public Envelope? AmpEnvelope { get; set; }

    /// <summary>Pitch bend range in semitones.</summary>
    public int PitchBendRange { get; set; } = 2;

    /// <summary>Velocity to volume amount 0..1, null when unknown.</summary>
    public double? VelocityToVolume { get; set; }

    /// <summary>Polyphony / max voices, 0 when unknown.</summary>
    public int MaxVoices { get; set; }

    public List<string> Warnings { get; } = [];

    public IEnumerable<SampleZone> AllZones => Groups.SelectMany(g => g.Zones);
}

public sealed class InstrumentMetadata
{
    public string Author { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string Library { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> Tags { get; } = [];
}

public enum PlayLogic
{
    Always,
    RoundRobin,
    Random
}

public sealed class SampleGroup
{
    public string Name { get; set; } = string.Empty;

    public bool IsReleaseTrigger { get; set; }

    public PlayLogic PlayLogic { get; set; } = PlayLogic.Always;

    /// <summary>1-based position in a round robin cycle, 0 when not set.</summary>
    public int RoundRobinPosition { get; set; }

    public List<SampleZone> Zones { get; } = [];
}

public enum LoopType
{
    Forward,
    Alternating
}

public sealed class SampleLoop
{
    public int Start { get; set; }

    /// <summary>Exclusive end frame (Start + Length).</summary>
    public int End { get; set; }

    public LoopType Type { get; set; } = LoopType.Forward;

    /// <summary>True = loop until note release, false = loop until the end of the envelope.</summary>
    public bool UntilRelease { get; set; }

    public int CrossfadeSamples { get; set; }

    /// <summary>Loop tuning in semitones.</summary>
    public double Tuning { get; set; }
}

public sealed class SampleZone
{
    /// <summary>The sample path as stored in the NKI (may be relative, absolute or just a file name).</summary>
    public string SamplePath { get; set; } = string.Empty;

    /// <summary>The resolved sample file on disk or null if it could not be found.</summary>
    public string? ResolvedSampleFile { get; set; }

    public int KeyLow { get; set; }
    public int KeyHigh { get; set; } = 127;
    public int KeyRoot { get; set; } = 60;
    public int VelocityLow { get; set; } = 1;
    public int VelocityHigh { get; set; } = 127;

    /// <summary>Kontakt style crossfades: fade widths measured inwards from the zone borders.</summary>
    public int KeyFadeLow { get; set; }
    public int KeyFadeHigh { get; set; }
    public int VelocityFadeLow { get; set; }
    public int VelocityFadeHigh { get; set; }

    public int SampleStart { get; set; }

    /// <summary>Exclusive end frame, -1 = end of the sample.</summary>
    public int SampleEnd { get; set; } = -1;

    /// <summary>Kontakt stores the sample end as an offset from the end of the sample; -1 when not used.</summary>
    public int SampleEndOffsetFromEnd { get; set; } = -1;

    /// <summary>Number of frames as stored in the NKI (Kontakt 5+), 0 if unknown.</summary>
    public int StoredFrames { get; set; }

    /// <summary>Gain in dB (instrument * group * zone).</summary>
    public double GainDb { get; set; }

    /// <summary>-1 (left) .. 1 (right).</summary>
    public double Pan { get; set; }

    /// <summary>Tuning in semitones (instrument * group * zone).</summary>
    public double Tuning { get; set; }

    public bool KeyTracking { get; set; } = true;

    public bool Reverse { get; set; }

    public bool OneShot { get; set; }

    public List<SampleLoop> Loops { get; } = [];
}

/// <summary>Times in seconds, levels 0..1, slope -1..1 (0 = linear).</summary>
public sealed class Envelope
{
    public double Attack { get; set; }
    public double Hold { get; set; }
    public double Decay { get; set; }
    public double Sustain { get; set; } = 1;
    public double Release { get; set; }
    public double AttackSlope { get; set; }
}
