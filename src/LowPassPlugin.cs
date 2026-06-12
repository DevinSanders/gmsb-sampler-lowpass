using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace LowPassPlugin;

/// <summary>
/// <see cref="IAudioSamplerPlugin"/> implementing a biquad low-pass
/// filter. The canonical "voice through walls" / "muffled distant
/// explosion" effect. Single knob: cutoff frequency (Hz, logarithmic
/// 100 – 20 000, default 8 000).
///
/// <para>DSP: direct-form-II transposed biquad with Butterworth
/// coefficients (Q = 1/√2) from Robert Bristow-Johnson's Audio EQ
/// Cookbook. Coefficients are recomputed on the UI thread on cutoff
/// change and published as an immutable record via
/// <see cref="Volatile.Write{T}(ref T,T)"/>. The audio thread reads
/// the reference with <see cref="Volatile.Read{T}(ref T)"/> once per
/// <see cref="ISampleProvider.Read(float[],int,int)"/> and uses
/// whichever snapshot it got.</para>
///
/// <para>Sample rate is hardcoded to the host's 48 kHz mixer contract.
/// Per-channel filter state (z1, z2) lives on the
/// <see cref="LowPassEffect"/> wrapper, not the instance, so two
/// attachments sharing the same <see cref="LowPassInstance"/> still
/// can't cross-contaminate state.</para>
/// </summary>
public sealed class LowPassPlugin : IAudioSamplerPlugin
{
    public string Id => "sampler.lowpass";
    public string Name => "Low-Pass Filter";
    public string Description => "Mutes high frequencies — 'voice through walls' / muffled distant sounds.";
    public string Version => PluginVersion.OfAssembly(typeof(LowPassPlugin));
    public string Author => "Devin Sanders";

    public SamplerAttachmentPoints SupportedAttachments => SamplerAttachmentPoints.All;

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public ISamplerInstance CreateInstance() => new LowPassInstance();
}

internal sealed class LowPassInstance : ISamplerInstance
{
    // Host mixer contract: 48 kHz IEEE-float stereo. Hardcoded so the
    // UI thread can rebuild coefficients without consulting the audio
    // graph.
    private const double SampleRate = 48000.0;

    // Butterworth: maximally-flat passband, 12 dB/oct rolloff.
    // Q = 1/√2 ≈ 0.7071.
    private const double Q = 0.70710678118654752440;

    internal const float MinCutoff = 100f;
    internal const float MaxCutoff = 20000f;
    internal const float DefaultCutoff = 8000f;

    // Float bit-pattern, accessed via Volatile.Read/Write. UI thread
    // writes when the user drags the slider or when the host pushes live
    // config via DeserializeConfig. Audio-thread reads happen indirectly
    // through Coefficients (which the UI thread also rebuilds on every
    // write).
    private int _cutoffBits = BitConverter.SingleToInt32Bits(DefaultCutoff);

    // Immutable coefficient record. Published via Volatile.Write of the
    // reference; the audio thread reads with Volatile.Read. Never
    // mutate the record's fields in place — always allocate a fresh one
    // and swap the reference.
    private Coefficients _coeffs;

    public LowPassInstance()
    {
        _coeffs = Coefficients.ForLowPass(SampleRate, DefaultCutoff, Q);
    }

    internal float Cutoff
        => BitConverter.Int32BitsToSingle(Volatile.Read(ref _cutoffBits));

    internal Coefficients CurrentCoefficients
        => Volatile.Read(ref _coeffs)!;

    /// <summary>UI-thread write. Updates both the cutoff bit-pattern and
    /// the coefficient snapshot; the audio thread will see the new
    /// snapshot on its next <c>Volatile.Read</c>. Clamps to the legal
    /// range so an out-of-range slider value or JSON payload can't
    /// produce nonsense coefficients.</summary>
    private void SetCutoff(float value)
    {
        var clamped = float.IsFinite(value)
            ? Math.Clamp(value, MinCutoff, MaxCutoff)
            : DefaultCutoff;
        Volatile.Write(ref _cutoffBits, BitConverter.SingleToInt32Bits(clamped));
        Volatile.Write(ref _coeffs, Coefficients.ForLowPass(SampleRate, clamped, Q));
    }

    public ISampleProvider CreateEffect(ISampleProvider source)
        => new LowPassEffect(source, this);

    public string SerializeConfig()
        => $"{{\"cutoff\":{Cutoff.ToString("R", CultureInfo.InvariantCulture)}}}";

    public void DeserializeConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            SetCutoff(DefaultCutoff);
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("cutoff", out var c)
                && c.ValueKind == JsonValueKind.Number
                && c.TryGetSingle(out var hz))
            {
                SetCutoff(hz);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Malformed config — keep current value. The host tolerates
            // this by contract; falling back to defaults would surprise
            // the user mid-edit. The ValueKind guard above handles the
            // common "cutoff is a string" case; the catch is belt-and-
            // suspenders for anything else the parser surfaces.
        }
    }

    public object? CreateControl()
    {
        // Avalonia is referenced but not redistributed (resolved from
        // the host's default ALC at runtime). Constructing controls here
        // runs on the UI thread.
        var panel = new global::Avalonia.Controls.StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Vertical,
            Spacing = 4,
        };

        var header = new global::Avalonia.Controls.TextBlock
        {
            Text = "Cutoff frequency",
        };

        var valueText = new global::Avalonia.Controls.TextBlock
        {
            Text = FormatHz(Cutoff),
        };

        // Linear 0..1 slider; we map it logarithmically to 100..20000
        // Hz on read/write so each unit of slider travel covers the same
        // *octave* of frequency. Hz is perceptually log-scaled, so a
        // linear-Hz slider would feel useless at the low end.
        var slider = new global::Avalonia.Controls.Slider
        {
            Minimum = 0.0,
            Maximum = 1.0,
            SmallChange = 0.01,
            LargeChange = 0.05,
            Value = HzToNorm(Cutoff),
        };

        slider.ValueChanged += (_, e) =>
        {
            var hz = (float)NormToHz(e.NewValue);
            SetCutoff(hz);
            valueText.Text = FormatHz(hz);
        };

        panel.Children.Add(header);
        panel.Children.Add(slider);
        panel.Children.Add(valueText);
        return panel;
    }

    public void Dispose() { }

    // ── Log mapping ─────────────────────────────────────────────────
    // hz = MinCutoff * (MaxCutoff/MinCutoff)^x  for x ∈ [0, 1]
    //   ⇒ x = log(hz / MinCutoff) / log(MaxCutoff / MinCutoff)

    internal static double HzToNorm(double hz)
        => Math.Log(Math.Clamp(hz, MinCutoff, MaxCutoff) / MinCutoff)
         / Math.Log(MaxCutoff / (double)MinCutoff);

    internal static double NormToHz(double x)
        => MinCutoff * Math.Pow(MaxCutoff / (double)MinCutoff, Math.Clamp(x, 0.0, 1.0));

    private static string FormatHz(float hz)
        => hz >= 1000f
            ? string.Create(CultureInfo.InvariantCulture, $"{hz / 1000f:0.##} kHz")
            : string.Create(CultureInfo.InvariantCulture, $"{hz:0} Hz");

    /// <summary>Immutable biquad low-pass coefficient set, normalised so
    /// a0 = 1. Audio EQ Cookbook (Robert Bristow-Johnson).</summary>
    internal sealed record Coefficients(float B0, float B1, float B2, float A1, float A2)
    {
        public static Coefficients ForLowPass(double sampleRate, double cutoffHz, double q)
        {
            // Clamp just below Nyquist so cos(ω0) → -1 doesn't blow up
            // the cookbook formula. At 48 kHz this leaves the 20 kHz
            // top of the knob comfortably inside the valid range.
            var fc = Math.Clamp(cutoffHz, 1.0, sampleRate * 0.49);
            var w0 = 2.0 * Math.PI * fc / sampleRate;
            var cosW0 = Math.Cos(w0);
            var sinW0 = Math.Sin(w0);
            var alpha = sinW0 / (2.0 * q);

            var b0 = (1.0 - cosW0) * 0.5;
            var b1 =  1.0 - cosW0;
            var b2 = (1.0 - cosW0) * 0.5;
            var a0 =  1.0 + alpha;
            var a1 = -2.0 * cosW0;
            var a2 =  1.0 - alpha;

            var inv = 1.0 / a0;
            return new Coefficients(
                (float)(b0 * inv),
                (float)(b1 * inv),
                (float)(b2 * inv),
                (float)(a1 * inv),
                (float)(a2 * inv));
        }
    }
}

/// <summary>Per-attachment biquad. Owns z1/z2 state; reads coefficients
/// from the parent <see cref="LowPassInstance"/> once per
/// <see cref="Read"/> call. Never allocates after construction.</summary>
internal sealed class LowPassEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly LowPassInstance _owner;
    private readonly int _channels;

    // Direct-form-II transposed state, per channel. Audio-thread-only —
    // no synchronisation needed.
    private readonly float[] _z1;
    private readonly float[] _z2;

    public LowPassEffect(ISampleProvider source, LowPassInstance owner)
    {
        _source = source;
        _owner = owner;
        _channels = source.WaveFormat.Channels;
        _z1 = new float[_channels];
        _z2 = new float[_channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var n = _source.Read(buffer, offset, count);
        if (n <= 0) return n;

        // Snapshot the coefficient record once per buffer. If the UI
        // thread swaps it mid-buffer we'll pick up the new set on the
        // next Read — well under 100 ms at the host's 48 kHz / typical
        // buffer sizes, which is the responsiveness target.
        var c = _owner.CurrentCoefficients;
        var ch = _channels;
        var b0 = c.B0; var b1 = c.B1; var b2 = c.B2;
        var a1 = c.A1; var a2 = c.A2;

        // Stride one interleaved frame at a time; one biquad per
        // channel. Hoisting z1/z2 into locals for the common stereo
        // case shaves a few array bounds checks.
        if (ch == 2)
        {
            float z1L = _z1[0], z2L = _z2[0];
            float z1R = _z1[1], z2R = _z2[1];
            int end = offset + n;
            for (int i = offset; i < end; i += 2)
            {
                var xL = buffer[i];
                var yL = b0 * xL + z1L;
                z1L = b1 * xL - a1 * yL + z2L;
                z2L = b2 * xL - a2 * yL;
                buffer[i] = yL;

                var xR = buffer[i + 1];
                var yR = b0 * xR + z1R;
                z1R = b1 * xR - a1 * yR + z2R;
                z2R = b2 * xR - a2 * yR;
                buffer[i + 1] = yR;
            }
            _z1[0] = z1L; _z2[0] = z2L;
            _z1[1] = z1R; _z2[1] = z2R;
        }
        else
        {
            for (int i = 0; i < n; i += ch)
            {
                for (int k = 0; k < ch; k++)
                {
                    var idx = offset + i + k;
                    var x = buffer[idx];
                    var y = b0 * x + _z1[k];
                    _z1[k] = b1 * x - a1 * y + _z2[k];
                    _z2[k] = b2 * x - a2 * y;
                    buffer[idx] = y;
                }
            }
        }

        return n;
    }
}
