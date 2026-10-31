"""Writes the trailer's background music: an original, calm electronic piece synthesized here, so it needs no licence.

The arrangement follows the trailer: soft pads under the title card, a beat and arpeggio while the app is shown,
and the last chord ringing out under the closing card.
Usage: python tools/store-media/music.py out.wav <seconds> [<intro end> <outro start>]
"""
import sys
import wave

import numpy as np

RATE = 48000
BEAT = 60 / 100  # 100 BPM
BAR = 4 * BEAT
CHORDS = [  # (bass root, pad voicing) as MIDI notes, two bars each: Dmaj9, Bm9, Gmaj9, Asus2
    (38, [57, 61, 64, 66, 69]),
    (35, [54, 57, 61, 62, 66]),
    (31, [54, 57, 59, 62, 66]),
    (33, [57, 59, 61, 64, 69]),
]
ARP = [0, 2, 4, 3, 1, 4, 2, 3]  # chord tones, an octave up, in eighth notes


def hz(note):
    return 440.0 * 2 ** ((note - 69) / 12)


def chord_at(t):
    return CHORDS[int(t // (2 * BAR)) % len(CHORDS)]


def envelope(n, attack, release):
    env = np.ones(n)
    a, r = min(n, int(attack * RATE)), min(n, int(release * RATE))
    env[:a] = np.linspace(0, 1, a) ** 2
    if r:
        env[-r:] *= np.linspace(1, 0, r) ** 2
    return env


def pad_note(note, seconds, rng):
    """A warm, slightly detuned pad: few soft harmonics, so it stays out of the way of the visuals."""
    t = np.arange(int(seconds * RATE)) / RATE
    out = np.zeros((len(t), 2))
    for side in range(2):
        for cents in (-7, 0, 6):
            f = hz(note) * 2 ** ((cents + (3 if side else -3)) / 1200)
            for k in range(1, 7):
                out[:, side] += np.sin(2 * np.pi * f * k * t + rng.uniform(0, 2 * np.pi)) * np.exp(-0.55 * k) / k
    shimmer = 1 + 0.08 * np.sin(2 * np.pi * 0.23 * t + rng.uniform(0, 6))
    return out * (envelope(len(t), 1.4, 1.6) * shimmer)[:, None]


def pluck(note, seconds=1.4):
    t = np.arange(int(seconds * RATE)) / RATE
    tone = sum(np.sin(2 * np.pi * hz(note) * k * t) * np.exp(-t * (3.2 + 2.2 * k)) / k ** 1.4 for k in range(1, 6))
    return tone * envelope(len(t), 0.004, 0.2)


def kick():
    t = np.arange(int(0.35 * RATE)) / RATE
    phase = 2 * np.pi * np.cumsum(48 + 80 * np.exp(-t * 28)) / RATE
    return np.sin(phase) * np.exp(-t * 9) * envelope(len(t), 0.002, 0.05)


def hat(rng):
    n = int(0.08 * RATE)
    noise = np.diff(rng.standard_normal(n + 2))  # high-passed...
    noise = (noise[1:] + noise[:-1]) / 2  # ...and with the harshest top end taken off
    return noise * np.exp(-np.arange(n) / RATE * 70) * 0.25


def add(track, sound, at, gain=1.0, pan=0.0):
    start = int(at * RATE)
    if start >= len(track):
        return
    sound = sound[: len(track) - start]
    if sound.ndim == 1:
        sound = np.stack([sound * (1 - pan) ** 0.5, sound * (1 + pan) ** 0.5], axis=1) / 2 ** 0.5
    track[start:start + len(sound)] += sound * gain


def reverb(signal, seconds=2.6, rng=None):
    """Convolution with a decaying stereo noise tail, darker as it fades."""
    n = int(seconds * RATE)
    t = np.arange(n) / RATE
    ir = rng.standard_normal((n, 2)) * np.exp(-t * 2.7)[:, None]
    for _ in range(3):  # soften the tail's brightness
        ir[1:] = 0.55 * ir[1:] + 0.45 * ir[:-1]
    ir /= np.sqrt((ir ** 2).sum(axis=0))
    size = 1 << int(np.ceil(np.log2(len(signal) + n)))
    wet = np.fft.irfft(np.fft.rfft(signal, size, axis=0) * np.fft.rfft(ir, size, axis=0), size, axis=0)
    return wet[: len(signal)]


def delay(signal, seconds, feedback=0.35, taps=4):
    out = np.zeros_like(signal)
    d = int(seconds * RATE)
    for i in range(1, taps + 1):
        gain = feedback ** i
        shifted = np.zeros_like(signal)
        shifted[d * i:] = signal[: len(signal) - d * i]
        out += shifted[:, ::-1] * gain if i % 2 else shifted * gain  # ping-pong
    return out


def render(duration, intro_end=3.2, outro_start=None):
    rng = np.random.default_rng(7)
    outro_start = duration - 3.6 if outro_start is None else outro_start
    n = int(duration * RATE)
    pads, plucks, drums, bass = (np.zeros((n, 2)) for _ in range(4))

    # Pads: one chord every two bars; the last one holds through the closing card.
    for at in np.arange(0, outro_start, 2 * BAR):
        for note in chord_at(at)[1]:
            add(pads, pad_note(note, min(2 * BAR, outro_start - at) + 1.6, rng), at, 0.055)
    for note in CHORDS[0][1]:
        add(pads, pad_note(note, duration - outro_start + 1.6, rng), outro_start, 0.055)

    # Bass and arpeggio from halfway through the intro; drums only while the app is on screen.
    step = BEAT / 2
    for i in range(int(duration / step)):
        at = i * step
        root, voicing = chord_at(at) if at < outro_start else CHORDS[0]
        if at >= intro_end * 0.5 and at < duration - 1.2:
            note = voicing[ARP[i % len(ARP)]] + 12
            add(plucks, pluck(note), at, 0.21 * min(1, (at - intro_end * 0.5) / 1.5 + 0.3), pan=0.35 if i % 2 else -0.35)
        if i % 8 == 0 and at >= intro_end * 0.5 and at < outro_start + BAR:
            tt = np.arange(int(2 * BAR * RATE)) / RATE
            low = np.sin(2 * np.pi * hz(root) * tt) + 0.35 * np.sin(4 * np.pi * hz(root) * tt)
            add(bass, low * envelope(len(tt), 0.08, 0.9), at, 0.11)
        if intro_end <= at < outro_start:
            if i % 4 == 0:
                add(drums, kick(), at, 0.32)
            if i % 2 == 1:
                add(drums, hat(rng), at, 0.16, pan=0.2)

    # The pads duck under each kick, which gives the calm piece a gentle pulse.
    pump = np.ones(n)
    tt = np.arange(int(BEAT * RATE)) / RATE
    for i in range(int(duration / BEAT)):
        at = i * BEAT
        if intro_end <= at < outro_start and i % 2 == 0:
            s = int(at * RATE)
            pump[s:s + len(tt)] = np.minimum(pump[s:s + len(tt)], (1 - 0.3 * np.exp(-tt * 7))[: n - s])
    pads *= pump[:, None]

    plucks += delay(plucks, 3 * BEAT / 4)
    dry = pads + plucks + bass + drums
    mix = dry + 0.32 * reverb(pads + plucks + 0.3 * drums, rng=rng)

    fade = np.ones(n)
    fade[: int(0.6 * RATE)] = np.linspace(0, 1, int(0.6 * RATE))
    tail = int(2.6 * RATE)
    fade[-tail:] *= np.linspace(1, 0, tail) ** 1.5
    mix *= fade[:, None]
    mix /= np.sqrt(np.mean(mix ** 2)) / 10 ** (-19 / 20)  # about -19 dBFS RMS: a background bed, not a song
    return np.tanh(mix * 1.1) / np.tanh(1.1)


def write_wav(path, audio):
    data = (np.clip(audio, -1, 1) * 32767 * 0.95).astype("<i2")
    with wave.open(path, "wb") as f:
        f.setnchannels(2)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(data.tobytes())


if __name__ == "__main__":
    args = [float(a) for a in sys.argv[2:]]
    write_wav(sys.argv[1], render(*args))
