"""Synthesizes the sound effects End K not ships without third-party audio.

Writes (16-bit PCM WAV):
  Resources/Sounds/AudienceShock.wav            viewer-intervention sting (2.4 s, 32 kHz mono)
  Resources/Sounds/Earthquake.wav               rumble with falling rocks (8 s, 32 kHz mono; the cutscene fades it out by 6 s)
  Resources/Sounds/Backrooms/lobby-ambient.wav  fluorescent-light hum, loops seamlessly (20 s, 24 kHz stereo)

Everything is generated from fixed seeds, so the output is the same on every run.
Usage: python tools/SfxSynth/make_sfx.py [<repo root>]   (needs numpy)
"""
import os
import sys
import wave

import numpy as np


def shaped_noise(n, rate, lo, hi, rng, tilt=0.0):
    """White noise band-limited to [lo, hi] Hz in the frequency domain. The result is circular (it loops)."""
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.fft.rfftfreq(n, 1.0 / rate)
    gain = ((f >= lo) & (f <= hi)).astype(float)
    if tilt:
        gain *= np.where(f > 0, (np.maximum(f, 1.0) / max(lo, 1.0)) ** tilt, 0.0)
    out = np.fft.irfft(spec * gain, n)
    return out / (np.max(np.abs(out)) + 1e-12)


def lowpass(x, rate, cutoff):
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1.0 / rate)
    return np.fft.irfft(spec / (1.0 + (f / cutoff) ** 4), len(x))


def smooth_random(n, rate, hz, rng):
    """Slow random control signal in [0, 1] (about `hz` changes per second)."""
    pts = max(int(n / rate * hz) + 2, 2)
    return np.interp(np.arange(n), np.linspace(0, n - 1, pts), rng.random(pts))


def comb_reverb(x, rate, decay=0.45, delays_ms=(29.7, 37.1, 41.1, 43.7)):
    out = np.copy(x)
    for d in delays_ms:
        k = int(rate * d / 1000.0)
        y = np.copy(x)
        for i in range(k, len(y)):
            y[i] += decay * y[i - k]
        out += y * 0.25
    return out


def normalize(x, peak_db, mean_db):
    """Scales to the target mean level, then limits the peak (soft clip above the peak target)."""
    rms = np.sqrt(np.mean(x ** 2)) + 1e-12
    x = x * (10 ** (mean_db / 20.0) / rms)
    peak = 10 ** (peak_db / 20.0)
    return np.tanh(x / peak) * peak


def write_wav(path, rate, data):
    data = np.atleast_2d(data)
    if data.shape[0] < data.shape[1]:
        data = data.T
    pcm = np.clip(np.round(data * 32767.0), -32768, 32767).astype('<i2')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with wave.open(path, 'wb') as w:
        w.setnchannels(pcm.shape[1])
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.tobytes())


def audience_shock():
    rate, dur = 32000, 2.4
    n = int(rate * dur)
    t = np.arange(n) / rate
    rng = np.random.default_rng(11)

    # impact: a low sine whose pitch drops 90 -> 38 Hz
    freq = 38 + 52 * np.exp(-t / 0.18)
    impact = np.sin(2 * np.pi * np.cumsum(freq) / rate) * np.exp(-t / 0.55)
    # transient crack at the start
    crack = shaped_noise(n, rate, 900, 7000, rng) * np.exp(-t / 0.035)
    # dissonant minor-second cluster that swells in and dies away
    cluster = np.zeros(n)
    for f0, ph in ((233.1, 0.0), (246.9, 1.3), (466.2, 0.7), (493.9, 2.1)):
        tone = np.sin(2 * np.pi * f0 * t + ph) + 0.35 * np.sin(4 * np.pi * f0 * t + ph)
        cluster += tone
    cluster *= (1 - np.exp(-t / 0.04)) * np.exp(-t / 0.9) * (0.85 + 0.15 * np.sin(2 * np.pi * 6.5 * t))
    mix = 1.0 * impact + 0.55 * crack + 0.22 * cluster
    mix = comb_reverb(mix, rate, decay=0.4)
    mix *= np.clip((dur - t) / 0.3, 0, 1)  # no click at the end
    return rate, normalize(mix, peak_db=-7.5, mean_db=-22.0)


def earthquake():
    rate, dur = 32000, 8.0
    n = int(rate * dur)
    t = np.arange(n) / rate
    rng = np.random.default_rng(23)

    # deep rumble: band-limited noise below ~110 Hz, its level wandering slowly
    rumble = lowpass(shaped_noise(n, rate, 18, 160, rng), rate, 110)
    rumble /= np.max(np.abs(rumble)) + 1e-12
    rumble *= 0.6 + 0.4 * smooth_random(n, rate, 3.0, rng)

    # grinding layer
    grind = shaped_noise(n, rate, 160, 900, rng, tilt=-1.0) * (0.3 + 0.7 * smooth_random(n, rate, 7.0, rng))

    # falling rocks: short gravelly bursts at random times
    rocks = np.zeros(n)
    gravel = shaped_noise(n, rate, 500, 4500, rng)
    for start in np.sort(rng.uniform(0.4, dur - 0.6, 46)):
        i = int(start * rate)
        m = int(rate * rng.uniform(0.04, 0.22))
        env = np.exp(-np.arange(m) / (m / 4.0))
        rocks[i:i + m] += gravel[i:i + m] * env * rng.uniform(0.3, 1.0)

    # heavy thumps
    thumps = np.zeros(n)
    for start in (0.15, 1.9, 3.4, 4.6):
        i = int(start * rate)
        m = int(rate * 0.9)
        tt = np.arange(m) / rate
        thumps[i:i + m] += np.sin(2 * np.pi * (30 + 25 * np.exp(-tt / 0.1)) * tt) * np.exp(-tt / 0.3)

    mix = 1.0 * rumble + 0.35 * grind + 0.4 * rocks + 0.8 * thumps
    env = np.clip(t / 0.6, 0, 1) * np.clip((dur - t) / 1.5, 0, 1)
    return rate, normalize(mix * env, peak_db=-1.0, mean_db=-12.5)


def lobby_ambient():
    rate, dur = 24000, 20.0
    n = int(rate * dur)
    t = np.arange(n) / rate
    rng = np.random.default_rng(37)
    chans = []
    for ch in range(2):
        # mains hum at 120 Hz with harmonics; every frequency fits a whole number of cycles in 20 s, so it loops
        hum = np.zeros(n)
        for k in range(1, 26):
            amp = (1.0 / k ** 1.1) * (1.6 if k % 2 == 1 else 0.7)
            hum += amp * np.sin(2 * np.pi * 120 * k * t + rng.uniform(0, 2 * np.pi))
        # ballast buzz: high band of noise gated by the 120 Hz cycle
        gate = np.clip(np.sin(2 * np.pi * 120 * t), 0, None) ** 6
        buzz = shaped_noise(n, rate, 2000, 7000, rng) * gate
        # slow flicker of the buzz level (periods dividing 20 s)
        flicker = 0.75 + 0.15 * np.sin(2 * np.pi * t / 5.0 + ch) + 0.1 * np.sin(2 * np.pi * t / 2.5)
        # quiet room tone
        room = shaped_noise(n, rate, 40, 3000, rng, tilt=-0.5)
        chans.append(0.55 * hum / np.max(np.abs(hum)) + 0.35 * buzz * flicker + 0.2 * room)
    data = np.stack(chans)
    rms = np.sqrt(np.mean(data ** 2))
    data *= 10 ** (-43.0 / 20.0) / rms
    peak = np.max(np.abs(data))
    limit = 10 ** (-21.5 / 20.0)
    if peak > limit:
        data *= limit / peak
    return rate, data


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', '..')
    sounds = os.path.join(root, 'Resources', 'Sounds')
    for rel, make in (('AudienceShock.wav', audience_shock),
                      ('Earthquake.wav', earthquake),
                      (os.path.join('Backrooms', 'lobby-ambient.wav'), lobby_ambient)):
        rate, data = make()
        path = os.path.join(sounds, rel)
        write_wav(path, rate, data)
        print(f'wrote {path}')


if __name__ == '__main__':
    main()
