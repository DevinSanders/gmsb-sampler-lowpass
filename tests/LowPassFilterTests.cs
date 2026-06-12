using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LowPassPlugin;
using NAudio.Wave;
using SoundBoard.PluginApi;
using Xunit;

namespace LowPassPlugin.Tests;

public class LowPassFilterTests
{
    // Host mixer contract: 48 kHz IEEE-float stereo.
    private static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private static ISamplerInstance NewInstance() => new LowPassPlugin().CreateInstance();

    // ── Factory isolation ───────────────────────────────────────────

    [Fact]
    public void CreateInstance_returns_distinct_objects()
    {
        var plugin = new LowPassPlugin();
        var a = plugin.CreateInstance();
        var b = plugin.CreateInstance();
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void Two_effects_do_not_share_filter_state()
    {
        // Drive a sine through one effect to build up z1/z2 history, then
        // confirm a freshly-created effect on a second instance still
        // starts from silence (no cross-contamination via statics).
        var noisy = NewInstance();
        noisy.DeserializeConfig("{\"cutoff\":500}");
        var noisyFx = noisy.CreateEffect(new SineProvider(Fmt, 8000));
        Render(noisyFx, frames: 48000); // warm it up, discard

        var fresh = NewInstance();
        fresh.DeserializeConfig("{\"cutoff\":500}");
        var freshFx = fresh.CreateEffect(new SilenceProvider(Fmt));
        var outBuf = Render(freshFx, frames: 512);

        // Silence in, fresh state ⇒ silence out. If state were shared
        // the primed history would ring into this buffer.
        Rms(outBuf).Should().BeApproximately(0f, 1e-6f);
    }

    // ── Config round-trip ───────────────────────────────────────────

    [Fact]
    public void Config_round_trips_cutoff()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("{\"cutoff\":5000}");
        var json = inst.SerializeConfig();

        ReadCutoff(json).Should().BeApproximately(5000f, 0.5f);
    }

    [Fact]
    public void Default_cutoff_is_8000()
    {
        ReadCutoff(NewInstance().SerializeConfig()).Should().BeApproximately(8000f, 0.5f);
    }

    [Theory]
    [InlineData(999999, 20000)] // above max clamps down
    [InlineData(1, 100)]        // below min clamps up
    [InlineData(-50, 100)]      // negative clamps up
    public void Out_of_range_cutoff_clamps(double input, float expected)
    {
        var inst = NewInstance();
        inst.DeserializeConfig($"{{\"cutoff\":{input.ToString(CultureInfo.InvariantCulture)}}}");
        ReadCutoff(inst.SerializeConfig()).Should().BeApproximately(expected, 0.5f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"cutoff\":\"oops\"}")]
    [InlineData("42")]
    public void Malformed_config_never_throws(string json)
    {
        var inst = NewInstance();
        var act = () => inst.DeserializeConfig(json);
        act.Should().NotThrow();
        // And the effect path still works afterwards.
        var fx = inst.CreateEffect(new SilenceProvider(Fmt));
        var read = () => Render(fx, 256);
        read.Should().NotThrow();
    }

    // ── Format preserved ────────────────────────────────────────────

    [Fact]
    public void CreateEffect_preserves_waveformat()
    {
        var src = new SineProvider(Fmt, 1000);
        var fx = NewInstance().CreateEffect(src);
        fx.WaveFormat.SampleRate.Should().Be(Fmt.SampleRate);
        fx.WaveFormat.Channels.Should().Be(Fmt.Channels);
        fx.WaveFormat.Encoding.Should().Be(Fmt.Encoding);
    }

    [Fact]
    public void Works_for_mono_sources_too()
    {
        var mono = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var inst = NewInstance();
        inst.DeserializeConfig("{\"cutoff\":500}");
        var fx = inst.CreateEffect(new SineProvider(mono, 8000));
        var act = () => Render(fx, 4096);
        act.Should().NotThrow();
    }

    // ── The filter actually filters ─────────────────────────────────

    [Fact]
    public void Rejects_frequencies_above_cutoff()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("{\"cutoff\":500}");
        var fx = inst.CreateEffect(new SineProvider(Fmt, 8000));

        var outBuf = RenderSettled(fx);
        // 8 kHz is ~4 octaves above a 500 Hz cutoff → ~48 dB down for a
        // 12 dB/oct Butterworth. Expect heavy attenuation.
        Rms(outBuf).Should().BeLessThan(0.05f);
    }

    [Fact]
    public void Passes_frequencies_below_cutoff()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("{\"cutoff\":8000}");
        var fx = inst.CreateEffect(new SineProvider(Fmt, 200));

        var outBuf = RenderSettled(fx);
        // 200 Hz well inside an 8 kHz passband ⇒ survives near full level.
        // A unit-amplitude sine has RMS ≈ 0.707.
        Rms(outBuf).Should().BeGreaterThan(0.6f);
    }

    [Fact]
    public void Lower_cutoff_attenuates_more()
    {
        // Same input tone, two cutoffs straddling it: the lower cutoff
        // must attenuate the tone more than the higher one.
        const double tone = 4000;

        var low = NewInstance();
        low.DeserializeConfig("{\"cutoff\":1000}");
        var lowRms = Rms(RenderSettled(low.CreateEffect(new SineProvider(Fmt, tone))));

        var high = NewInstance();
        high.DeserializeConfig("{\"cutoff\":16000}");
        var highRms = Rms(RenderSettled(high.CreateEffect(new SineProvider(Fmt, tone))));

        lowRms.Should().BeLessThan(highRms);
    }

    // ── Live-config smoke ───────────────────────────────────────────

    [Fact]
    public async Task Hammering_DeserializeConfig_while_reading_never_throws()
    {
        var inst = NewInstance();
        var fx = inst.CreateEffect(new SineProvider(Fmt, 1000));

        using var cts = new CancellationTokenSource();
        Exception? failure = null;

        var writer = Task.Run(() =>
        {
            var rng = new Random(1234);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var hz = 100 + rng.NextDouble() * 19900;
                    inst.DeserializeConfig(
                        $"{{\"cutoff\":{hz.ToString(CultureInfo.InvariantCulture)}}}");
                }
            }
            catch (Exception ex) { failure = ex; }
        }, cts.Token);

        var buf = new float[2048];
        for (int i = 0; i < 5000; i++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            fx.Read(buf, 0, buf.Length);
            foreach (var s in buf)
                float.IsNaN(s).Should().BeFalse("audio must stay finite during live config swaps");
        }

        cts.Cancel();
        await writer;
        failure.Should().BeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static float ReadCutoff(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("cutoff").GetSingle();
    }

    /// <summary>Render <paramref name="frames"/> frames through the effect
    /// and return the interleaved buffer.</summary>
    private static float[] Render(ISampleProvider fx, int frames)
    {
        var buf = new float[frames * fx.WaveFormat.Channels];
        int total = 0;
        while (total < buf.Length)
            total += fx.Read(buf, total, buf.Length - total);
        return buf;
    }

    /// <summary>Render through the effect, discarding a settling prefix so
    /// the biquad's transient doesn't skew the RMS, and return the steady
    /// tail.</summary>
    private static float[] RenderSettled(ISampleProvider fx)
    {
        Render(fx, frames: 24000); // 0.5 s settle, discarded
        return Render(fx, frames: 24000);
    }

    private static float Rms(float[] buf)
    {
        double acc = 0;
        for (int i = 0; i < buf.Length; i++) acc += (double)buf[i] * buf[i];
        return (float)Math.Sqrt(acc / buf.Length);
    }
}

/// <summary>Unit-amplitude sine, identical on every channel.</summary>
internal sealed class SineProvider(WaveFormat fmt, double freq) : ISampleProvider
{
    private long _frame;
    public WaveFormat WaveFormat => fmt;

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = fmt.Channels;
        for (int i = 0; i < count; i += ch)
        {
            var s = (float)Math.Sin(2.0 * Math.PI * freq * _frame / fmt.SampleRate);
            for (int k = 0; k < ch; k++) buffer[offset + i + k] = s;
            _frame++;
        }
        return count;
    }
}

internal sealed class SilenceProvider(WaveFormat fmt) : ISampleProvider
{
    public WaveFormat WaveFormat => fmt;
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}
