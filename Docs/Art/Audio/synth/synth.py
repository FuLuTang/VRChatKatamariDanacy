#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""VRC塊魂 第一版占位音效生成器（纯程序化、可复现）。

本脚本把 [../README.md](../README.md) 中的 SFX 表渲染成 48 kHz / 16-bit / 单声道 WAV，
输出到 ``../rendered/``，并写出 ``manifest.csv``。全部声音由本文件的振荡器、噪声与
包络合成，**不含任何第三方采样**；因此不引入授权问题。这些是用于在 Unity / VRChat
里判断时机与手感的占位音，不是最终音质。

约束与约定
----------
* 仅用 Python 3 标准库（wave / math / random / array / csv）。numpy 不是必需依赖：
  本机 ``python3 -c "import numpy"`` 失败，生成器不引入它，也没有第二套实现。
* 每个声音一个函数；``SOUND_TABLE`` 驱动渲染，``seed`` 固定，输出逐字节确定。
* 采样率 48 kHz，单声道，16-bit PCM；每个文件目标峰值见 ``target``（≤ 0.89，留余量）。
* 循环音（roll / magnet loop）用"周期化合成"：所有振荡器频率、LFO 速率都取整数个
  周期/循环，噪声用环形单极点滤波（把 IIR 跑满两圈，取最后一圈），使首尾样本按周期
  延拓连续。``test_synth.py`` 用接缝跳变与相邻样本差的比值来验证。
* 音量语义：``0 dBFS`` = 数字满刻度；峰值一律 < -1 dBFS。响度目标写在 README，
  本文件只报告 peak / RMS，不声称测得 LUFS（stdlib 没有响度表）。

用法::

    python3 synth.py                     # 渲染全部到 ../rendered/
    python3 synth.py --out /tmp/audiotest
    python3 synth.py --only roll,pickup  # 名字里含这些子串的条目
"""

import argparse
import array
import csv
import math
import os
import random
import sys
import wave

SR = 48000
TAU = 2.0 * math.pi
PEAK_LIMIT = 0.89  # -1.01 dBFS，README 的硬上限

#: 归一化球径：0 = 小（初始球芯），1 = 大（接近当局上限）。仅用于参数映射。
SIZE_SMALL, SIZE_MEDIUM, SIZE_LARGE = 0.05, 0.50, 1.00


# ---------------------------------------------------------------------------
# DSP helpers
# ---------------------------------------------------------------------------

def frames(duration_s):
    """秒 -> 采样数（至少 1）。"""
    return max(1, int(round(duration_s * SR)))


def snap_freq(freq, n):
    """把频率对齐到 n 个采样里恰好整数个周期，供无缝循环使用。"""
    cycles = max(1, int(round(freq * n / SR)))
    return cycles * SR / float(n)


def sine(n, freq, phase=0.0):
    w = TAU * freq / SR
    return [math.sin(w * i + phase) for i in range(n)]


def sweep(n, f0, f1, curve="exp", phase=0.0):
    """单音扫频。curve='exp' 为等比（听感均匀），'lin' 为线性。"""
    out = [0.0] * n
    ph = phase
    for i in range(n):
        t = i / float(n - 1) if n > 1 else 0.0
        f = f0 * (f1 / f0) ** t if curve == "exp" else f0 + (f1 - f0) * t
        out[i] = math.sin(ph)
        ph += TAU * f / SR
    return out


def white(n, rng):
    return [rng.uniform(-1.0, 1.0) for _ in range(n)]


def lp1(x, cutoff):
    """一极点低通（因果版，用于一次性音效的起音瞬态可接受）。"""
    a = math.exp(-TAU * max(10.0, cutoff) / SR)
    y = 0.0
    out = [0.0] * len(x)
    for i, v in enumerate(x):
        y = a * y + (1.0 - a) * v
        out[i] = y
    return out


def lp1_circular(x, cutoff, laps=2):
    """环形一极点低通：输出在 x 的周期延拓上连续。

    把递推在环形缓冲上跑 ``laps`` 圈并只保留最后一圈，相当于求环形稳态解；
    误差按 ``a**n`` 衰减（48 kHz、截止 200 Hz 时一圈内已到机器精度）。
    """
    n = len(x)
    a = math.exp(-TAU * max(10.0, cutoff) / SR)
    y = 0.0
    out = [0.0] * n
    for lap in range(laps):
        last = lap == laps - 1
        for i in range(n):
            y = a * y + (1.0 - a) * x[i]
            if last:
                out[i] = y
    return out


def lp1_varying(x, cutoff_at):
    """截止频率随时间变化的低通；cutoff_at(i) 返回该采样处的 Hz。"""
    y = 0.0
    out = [0.0] * len(x)
    for i, v in enumerate(x):
        a = math.exp(-TAU * max(20.0, cutoff_at(i)) / SR)
        y = a * y + (1.0 - a) * v
        out[i] = y
    return out


def highpass_complement(x, cutoff):
    """用输入减去环形低通，得到"去掉低频"的频带（循环安全）。"""
    low = lp1_circular(x, cutoff)
    return [v - l for v, l in zip(x, low)]


def env_perc(n, attack_s, tau_s):
    """打击包络：短起音 + 指数衰减。"""
    na = max(1, frames(attack_s))
    out = [0.0] * n
    for i in range(n):
        a = i / float(na) if i < na else 1.0
        out[i] = a * math.exp(-max(0, i - na) / (SR * tau_s))
    return out


def env_rise_fall(n, rise_s, fall_s, floor=0.0):
    """余弦上升/下降，用于"吸气"、"垫音"类长音。"""
    nr = max(1, frames(rise_s))
    nf = max(1, frames(fall_s))
    out = [0.0] * n
    for i in range(n):
        r = 0.5 - 0.5 * math.cos(math.pi * min(1.0, i / float(nr)))
        f = 0.5 - 0.5 * math.cos(math.pi * min(1.0, (n - 1 - i) / float(nf)))
        out[i] = floor + (1.0 - floor) * r * f
    return out


def fade_edges(x, fade_in_s=0.0, fade_out_s=0.0):
    n = len(x)
    ni = min(n, frames(fade_in_s)) if fade_in_s > 0 else 0
    no = min(n, frames(fade_out_s)) if fade_out_s > 0 else 0
    for i in range(ni):
        x[i] *= 0.5 - 0.5 * math.cos(math.pi * i / ni)
    for i in range(no):
        x[n - 1 - i] *= 0.5 - 0.5 * math.cos(math.pi * i / no)
    return x


def mix(*layers):
    """layers: (signal, gain) 元组；返回逐样本和（长度取最短）。"""
    if not layers:
        return []
    n = min(len(sig) for sig, _ in layers)
    out = [0.0] * n
    for sig, gain in layers:
        for i in range(n):
            out[i] += sig[i] * gain
    return out


def add_into(dst, src, gain, offset=0):
    """把 src 以 gain 叠加到 dst 的 offset 处（越界部分丢弃）。"""
    for i, v in enumerate(src):
        j = offset + i
        if j >= len(dst):
            break
        dst[j] += v * gain
    return dst


def dc_block(x):
    """去掉直流分量：长循环的 DC 会白占余量，短促脉冲也可能留下偏置。"""
    if not x:
        return x
    m = sum(x) / len(x)
    return [v - m for v in x]


def peak(x):
    return max((abs(v) for v in x), default=0.0)


def rms(x):
    if not x:
        return 0.0
    return math.sqrt(sum(v * v for v in x) / len(x))


def normalize(x, target):
    p = peak(x)
    if p <= 0.0:
        raise ValueError("silent buffer")
    g = target / p
    return [v * g for v in x]


def quantize(x):
    """Float -> 16-bit PCM（小端由 array + byteswap 处理）。"""
    out = array.array("h")
    for v in x:
        q = int(round(max(-1.0, min(1.0, v)) * 32767.0))
        out.append(q)
    if sys.byteorder == "big":
        out.byteswap()
    return out


def write_wav(path, pcm):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def db(x, floor=1e-9):
    """线性幅度 -> dBFS；0 记作 -inf。"""
    return 20.0 * math.log10(x) if x > floor else float("-inf")


# ---------------------------------------------------------------------------
# 单个声音
# ---------------------------------------------------------------------------

def _roll_loop(seed, size_t, duration_s):
    """滚动底噪：低频隆隆 + 颗粒噪声 + 周期性"压过地面"脉冲。

    频率、LFO 速率与脉冲间距都取循环长度的整数分频，因此首尾可以无缝咬合。
    速度不烘焙进文件：玩法层用播放速率/音量/低通随速度变化（见 README §4）。
    """
    n = frames(duration_s)
    rng = random.Random(seed)

    f0 = snap_freq(90.0 - 50.0 * size_t, n)  # 小 90 Hz -> 大 40 Hz
    core = [0.0] * n
    for k, g in ((1, 0.55), (2, 0.24), (3, 0.10), (4, 0.045)):
        w = TAU * snap_freq(f0 * k, n) / SR
        ph = rng.uniform(0.0, TAU)
        for i in range(n):
            core[i] += g * math.sin(w * i + ph)

    # 颗粒：白噪 -> 环形带通（约 180 Hz .. 2.2 kHz）-> 双 LFO 幅度调制
    src = white(n, rng)
    band = highpass_complement(lp1_circular(src, 180.0 + 500.0 * (1.0 - size_t)), 180.0)
    rate_a = max(2, int(round(9.0 - 2.0 * size_t)))   # 每循环的整数周期数
    rate_b = max(3, int(round(15.0 - 3.0 * size_t)))
    grain = [0.0] * n
    for i in range(n):
        ma = 0.5 + 0.5 * math.sin(TAU * rate_a * i / n)
        mb = 0.5 + 0.5 * math.sin(TAU * rate_b * i / n + 1.7)
        grain[i] = band[i] * (0.30 + 0.55 * ma * (0.55 + 0.45 * mb))

    # 脉冲：每循环 k 次，短促低频"压地"感
    k = max(6, int(round(48.0 - 14.0 * size_t)))     # 小 47 次 -> 大 34 次
    step = max(1, n // k)
    burst_n = frames(0.008 + 0.006 * (1.0 - size_t))
    burst = mix(
        (sine(burst_n, 120.0 + 60.0 * (1.0 - size_t)), 0.55),
        (lp1(white(burst_n, rng), 700.0), 0.45),
    )
    env = env_perc(burst_n, 0.0008, 0.004)
    burst = [b * e for b, e in zip(burst, env)]
    pulses = [0.0] * n
    for pos in range(0, n - burst_n + 1, step):
        add_into(pulses, burst, 0.55, pos)

    out = mix((core, 0.75), (grain, 0.55), (pulses, 0.45))
    out = lp1_circular(out, 3200.0 - 1400.0 * size_t)  # 大球更闷
    return out


def sfx_roll_small(seed):
    return _roll_loop(seed, SIZE_SMALL, 1.50)


def sfx_roll_medium(seed):
    return _roll_loop(seed, SIZE_MEDIUM, 1.80)


def sfx_roll_large(seed):
    return _roll_loop(seed, SIZE_LARGE, 2.20)


_PICKUP_BASE = (470.0, 545.0, 630.0, 725.0, 835.0)


def _pickup_small(seed, variant):
    """小物拾取：短促的"啵"，略上滑，按变体升调、明暗略有差别。"""
    base = _PICKUP_BASE[variant]
    n = frames(0.13)
    rng = random.Random(seed)
    body = sweep(n, base * 0.86, base * 1.42, curve="exp")
    body = [b * e for b, e in zip(body, env_perc(n, 0.0015, 0.038))]
    click = lp1(white(n, rng), 4200.0 - 500.0 * variant)
    click = [c * e for c, e in zip(click, env_perc(n, 0.0004, 0.006))]
    sweet = sine(n, base * 2.02)
    sweet = [s * e for s, e in zip(sweet, env_perc(n, 0.001, 0.018))]
    out = mix((body, 1.0), (click, 0.30), (sweet, 0.14))
    out = lp1(out, 7200.0 - 600.0 * variant)
    return fade_edges(out, 0.0005, 0.010)


def sfx_pickup_01(seed):
    return _pickup_small(seed, 0)


def sfx_pickup_02(seed):
    return _pickup_small(seed, 1)


def sfx_pickup_03(seed):
    return _pickup_small(seed, 2)


def sfx_pickup_04(seed):
    return _pickup_small(seed, 3)


def sfx_pickup_05(seed):
    return _pickup_small(seed, 4)


def sfx_pickup_big(seed):
    """大物拾取：低频"咚"+ 一团低通噪声，无刺耳瞬态。"""
    n = frames(0.36)
    rng = random.Random(seed)
    sub = sweep(n, 128.0, 46.0, curve="exp")
    sub = [s * e for s, e in zip(sub, env_perc(n, 0.004, 0.10))]
    body = lp1(white(n, rng), 620.0)
    body = [b * e for b, e in zip(body, env_perc(n, 0.002, 0.055))]
    knock = sine(n, 180.0)
    knock = [k * e for k, e in zip(knock, env_perc(n, 0.001, 0.022))]
    out = mix((sub, 1.0), (body, 0.55), (knock, 0.22))
    return fade_edges(out, 0.001, 0.05)


def sfx_bump_reject(seed):
    """撞上"太大、不能吸"的东西 / 裁决拒绝：中性闷响，不是奖励音。"""
    n = frames(0.20)
    rng = random.Random(seed)
    thud = sweep(n, 230.0, 150.0, curve="exp")
    thud = [t * e for t, e in zip(thud, env_perc(n, 0.003, 0.045))]
    body = lp1(white(n, rng), 900.0)
    body = [b * e for b, e in zip(body, env_perc(n, 0.002, 0.030))]
    out = mix((thud, 1.0), (body, 0.45))
    out = lp1(out, 2400.0)  # 明确"钝"，不要把拒绝听成失败号角
    return fade_edges(out, 0.001, 0.04)


def sfx_growth_step(seed):
    """成长档位：三音上行小阶梯，只表示档位变化，不做长庆祝。"""
    n = frames(0.50)
    out = [0.0] * n
    notes = ((0.0, 523.25, 0.22), (0.055, 659.25, 0.22), (0.110, 783.99, 0.34))
    for offset, freq, tau in notes:
        m = frames(0.40)
        bell = mix(
            (sine(m, freq), 1.0),
            (sine(m, freq * 2.0), 0.20),
            (sine(m, freq * 3.01), 0.07),
        )
        bell = [b * e for b, e in zip(bell, env_perc(m, 0.003, tau))]
        add_into(out, bell, 0.55, frames(offset))
    return fade_edges(out, 0.001, 0.05)


def sfx_bank_deposit(seed):
    """交分成功：原创四音上行（C5-E5-G5-C6）+ 低频落地感。"""
    n = frames(0.90)
    out = [0.0] * n
    notes = ((0.00, 523.25, 0.26), (0.07, 659.25, 0.26),
             (0.14, 783.99, 0.28), (0.215, 1046.50, 0.46))
    for offset, freq, tau in notes:
        m = frames(0.66)
        bell = mix(
            (sine(m, freq), 1.0),
            (sine(m, freq * 2.0), 0.22),
            (sine(m, freq * 3.01), 0.08),
            (sine(m, freq * 4.20), 0.035),
        )
        bell = [b * e for b, e in zip(bell, env_perc(m, 0.004, tau))]
        add_into(out, bell, 0.50, frames(offset))
    # 落地"存进去"的软低频
    m = frames(0.40)
    land = [s * e for s, e in zip(sweep(m, 140.0, 70.0), env_perc(m, 0.006, 0.09))]
    add_into(out, land, 0.35, frames(0.20))
    return fade_edges(out, 0.001, 0.06)


def sfx_magnet_activate(seed):
    """磁铁开启：短促"吸气"上扫，告诉玩家和对手磁铁生效。"""
    n = frames(0.50)
    rng = random.Random(seed)
    tone = sweep(n, 165.0, 520.0, curve="exp")
    tone_env = env_rise_fall(n, 0.22, 0.10, floor=0.05)
    tone = [t * e for t, e in zip(tone, tone_env)]
    src = white(n, rng)
    air = lp1_varying(src, lambda i: 250.0 + 3000.0 * (i / float(n)) ** 1.6)
    air = [a * e for a, e in zip(air, env_rise_fall(n, 0.30, 0.08, floor=0.0))]
    out = mix((tone, 0.60), (air, 0.75))
    out = lp1(out, 5200.0)
    return fade_edges(out, 0.002, 0.02)


def sfx_magnet_loop(seed):
    """磁铁持续：低鸣循环，能量低、可被引擎整体淡出；不参与优先级抢占。"""
    n = frames(1.60)
    rng = random.Random(seed)
    f1 = snap_freq(58.0, n)
    hum = [0.0] * n
    for k, g in ((1, 0.55), (2, 0.22), (3, 0.08)):
        w = TAU * snap_freq(f1 * k, n) / SR
        ph = rng.uniform(0.0, TAU)
        for i in range(n):
            hum[i] += g * math.sin(w * i + ph)
    wobble = [0.55 + 0.30 * math.sin(TAU * 7 * i / n + 0.6) *
              (0.6 + 0.4 * math.sin(TAU * 3 * i / n)) for i in range(n)]
    src = white(n, rng)
    band = highpass_complement(lp1_circular(src, 420.0), 200.0)
    band = [b * 0.35 * w for b, w in zip(band, wobble)]
    out = mix((hum, 0.8), (band, 0.5))
    out = lp1_circular(out, 1200.0)
    return out


def sfx_rocket_boost(seed):
    """火箭启动：噪声"呼"上扫 + 低频推背，约 1 s 内收住，不做持续轰鸣。"""
    n = frames(0.95)
    rng = random.Random(seed)
    src = white(n, rng)
    air = lp1_varying(
        src,
        lambda i: 240.0 + 3400.0 * math.sin(math.pi * min(1.0, i / (0.55 * n))) ** 0.8,
    )
    air_env = env_rise_fall(n, 0.05, 0.45, floor=0.0)
    air = [a * e for a, e in zip(air, air_env)]
    sub = sweep(n, 96.0, 52.0, curve="exp")
    sub = [s * e for s, e in zip(sub, env_rise_fall(n, 0.04, 0.5))]
    push = sweep(n, 160.0, 300.0, curve="lin")
    push = [p * e for p, e in zip(push, env_rise_fall(n, 0.10, 0.35))]
    out = mix((air, 0.85), (sub, 0.55), (push, 0.30))
    out = lp1(out, 6000.0)
    return fade_edges(out, 0.002, 0.05)


def sfx_swallow_ko(seed):
    """被吞 / KO：下行"咕噜"+ 软低频，不用爆裂声，不吓人。"""
    n = frames(1.10)
    rng = random.Random(seed)
    gulp = sweep(n, 430.0, 105.0, curve="exp")
    gulp = [g * e for g, e in zip(gulp, env_perc(n, 0.006, 0.16))]
    gulp2 = sweep(n, 240.0, 70.0, curve="exp")
    gulp2 = [g * e for g, e in zip(gulp2, env_perc(n, 0.010, 0.22))]
    m = frames(0.80)
    boom = sweep(m, 70.0, 48.0, curve="exp")
    boom = [b * e for b, e in zip(boom, env_perc(m, 0.010, 0.30))]
    thump = lp1(white(m, rng), 420.0)
    thump = [t * e for t, e in zip(thump, env_perc(m, 0.004, 0.10))]
    out = mix((gulp, 0.65), (gulp2, 0.40))
    add_into(out, boom, 0.55, frames(0.10))
    add_into(out, thump, 0.35, frames(0.10))
    out = lp1(out, 2600.0)
    return fade_edges(out, 0.003, 0.10)


def sfx_respawn(seed):
    """重生：安静的"回到场上"，上行软垫 + 一个轻铃，不是庆祝。"""
    n = frames(1.20)
    rng = random.Random(seed)
    pad = [0.0] * n
    for freq, gain in ((196.0, 0.5), (293.66, 0.35), (392.0, 0.22)):
        glide = sweep(n, freq * 0.94, freq * 1.06, curve="lin")
        pad = [p + glide[i] * gain for i, p in enumerate(pad)]
    pad = [p * e for p, e in zip(pad, env_rise_fall(n, 0.22, 0.55, floor=0.0))]
    shimmer = highpass_complement(white(n, rng), 3000.0)
    shimmer = [s * e * 0.10 for s, e in zip(shimmer, env_rise_fall(n, 0.30, 0.5))]
    m = frames(0.55)
    bell = mix((sine(m, 783.99), 1.0), (sine(m, 783.99 * 2.0), 0.18))
    bell = [b * e for b, e in zip(bell, env_perc(m, 0.006, 0.16))]
    out = mix((pad, 0.55), (shimmer, 0.5))
    add_into(out, bell, 0.28, frames(0.62))
    out = lp1(out, 5200.0)
    return fade_edges(out, 0.01, 0.08)


def sfx_round_countdown(seed):
    """倒计时滴答：单音短哔；最后几秒由引擎整体升 2 个半音并略微加长。"""
    n = frames(0.14)
    beep = mix((sine(n, 880.0), 1.0), (sine(n, 1760.0), 0.18))
    beep = [b * e for b, e in zip(beep, env_perc(n, 0.004, 0.045))]
    out = lp1(beep, 5200.0)
    return fade_edges(out, 0.002, 0.02)


def sfx_round_start(seed):
    """开局：三音上行 + 低频底，短促、封顶音量。"""
    n = frames(1.10)
    rng = random.Random(seed)
    out = [0.0] * n
    notes = ((0.00, 392.00, 0.26), (0.13, 523.25, 0.26), (0.26, 659.25, 0.55))
    for offset, freq, tau in notes:
        m = frames(0.80)
        voice = [0.0] * m
        for k in range(1, 7):
            w = TAU * freq * k / SR
            g = 1.0 / k
            for i in range(m):
                voice[i] += g * math.sin(w * i)
        voice = lp1(voice, 2800.0)
        voice = [v * e for v, e in zip(voice, env_perc(m, 0.010, tau))]
        add_into(out, voice, 0.42, frames(offset))
    bed = [s * e for s, e in zip(sine(n, 98.0), env_rise_fall(n, 0.03, 0.45))]
    out = mix((out, 1.0), (bed, 0.18))
    return fade_edges(out, 0.002, 0.06)


def sfx_round_end(seed):
    """回合结束 / 结果提交：下行解决终止式，不提前宣告赢家。"""
    n = frames(1.70)
    out = [0.0] * n
    notes = ((0.00, 659.25, 0.30), (0.18, 587.33, 0.30),
             (0.36, 523.25, 0.30), (0.56, 392.00, 0.85))
    for offset, freq, tau in notes:
        m = frames(1.10)
        bell = mix(
            (sine(m, freq), 1.0),
            (sine(m, freq * 2.0), 0.20),
            (sine(m, freq * 3.0), 0.07),
        )
        bell = [b * e for b, e in zip(bell, env_perc(m, 0.008, tau))]
        add_into(out, bell, 0.42, frames(offset))
    bed = [s * e for s, e in zip(sine(n, 98.0), env_rise_fall(n, 0.10, 0.9))]
    out = mix((out, 1.0), (bed, 0.16))
    out = lp1(out, 4200.0)
    return fade_edges(out, 0.004, 0.12)


def sfx_threat_warning(seed):
    """对手威胁：两声中音上行（A4 → D5），中频为主、无尖叫高频。

    这是 P0 提示音，不随音量设置以外的装饰变化；引擎侧限速重复，不做循环。
    """
    n = frames(0.55)
    out = [0.0] * n
    for offset, freq in ((0.00, 440.0), (0.16, 587.33)):
        m = frames(0.28)
        voice = mix(
            (sine(m, freq), 1.0),
            (sine(m, freq * 2.0), 0.25),
            (sine(m, freq * 3.0), 0.06),
        )
        voice = [v * e for v, e in zip(voice, env_perc(m, 0.006, 0.075))]
        add_into(out, voice, 0.48, frames(offset))
    m = frames(0.30)
    bed = [s * e for s, e in zip(sine(m, 110.0), env_perc(m, 0.004, 0.10))]
    add_into(out, bed, 0.16, 0)
    out = lp1(out, 3600.0)
    return fade_edges(out, 0.002, 0.03)


def sfx_ui_click(seed):
    """UI 点击：短促、低亮度，音量明显低于玩法音。"""
    n = frames(0.055)
    rng = random.Random(seed)
    body = mix((sine(n, 1350.0), 1.0), (sine(n, 2700.0), 0.12))
    body = [b * e for b, e in zip(body, env_perc(n, 0.0015, 0.012))]
    tick = lp1(white(n, rng), 5200.0)
    tick = [t * e for t, e in zip(tick, env_perc(n, 0.0004, 0.003))]
    out = mix((body, 0.5), (tick, 0.25))
    return fade_edges(out, 0.0005, 0.006)


def sfx_ui_hover(seed):
    """UI 悬停：更轻更短，避免连续悬停时吵闹。"""
    n = frames(0.035)
    body = sine(n, 1850.0)
    body = [b * e for b, e in zip(body, env_perc(n, 0.001, 0.009))]
    return fade_edges(body, 0.0005, 0.005)


# ---------------------------------------------------------------------------
# 渲染表：file / 事件映射 / 循环 / 目标峰值 / seed
# ---------------------------------------------------------------------------

def _entry(file, fn, seed, loop, target, events, label, note=""):
    return dict(file=file, fn=fn, seed=seed, loop=loop, target=target,
                events=events, label=label, note=note)


SOUND_TABLE = (
    _entry("sfx_roll_small.wav", sfx_roll_small, 1101, True, 0.50,
           "round.started;round.settling", "滚动底噪（小）",
           "循环 1.50 s；标称速度，引擎按速度变调/变低通"),
    _entry("sfx_roll_medium.wav", sfx_roll_medium, 1102, True, 0.50,
           "round.started;round.settling", "滚动底噪（中）",
           "循环 1.80 s；与小球交叉淡化 0.4 s"),
    _entry("sfx_roll_large.wav", sfx_roll_large, 1103, True, 0.50,
           "round.started;round.settling", "滚动底噪（大）",
           "循环 2.20 s；限制最高频，避免 VR 齿音"),
    _entry("sfx_pickup_01.wav", sfx_pickup_01, 1201, False, 0.78,
           "collect.confirmed", "小物拾取 1/5", "按物品稳定 ID 取变体，不运行时随机"),
    _entry("sfx_pickup_02.wav", sfx_pickup_02, 1202, False, 0.78,
           "collect.confirmed", "小物拾取 2/5", ""),
    _entry("sfx_pickup_03.wav", sfx_pickup_03, 1203, False, 0.78,
           "collect.confirmed", "小物拾取 3/5", ""),
    _entry("sfx_pickup_04.wav", sfx_pickup_04, 1204, False, 0.78,
           "collect.confirmed", "小物拾取 4/5", ""),
    _entry("sfx_pickup_05.wav", sfx_pickup_05, 1205, False, 0.78,
           "collect.confirmed", "小物拾取 5/5", ""),
    _entry("sfx_pickup_big.wav", sfx_pickup_big, 1210, False, 0.80,
           "collect.confirmed", "大物拾取", "低频重音；150 ms 合成窗口内合并"),
    _entry("sfx_bump_reject.wav", sfx_bump_reject, 1301, False, 0.62,
           "collect.rejected;bank.rejected", "不能吸/裁决拒绝",
           "中性闷响，绝不使用奖励音；也用于 bank.rejected 的临时替代"),
    _entry("sfx_growth_step.wav", sfx_growth_step, 1310, False, 0.70,
           "growth.threshold", "成长档位阶梯", "一次提交跨多档时只播最终档位"),
    _entry("sfx_bank_deposit.wav", sfx_bank_deposit, 1401, False, 0.80,
           "bank.committed", "交分成功", "按 bankRequestId 恰播一次"),
    _entry("sfx_magnet_activate.wav", sfx_magnet_activate, 1501, False, 0.72,
           "magnet.activated;powerup.acquired", "磁铁开启", "短启动音，不含循环"),
    _entry("sfx_magnet_loop.wav", sfx_magnet_loop, 1502, True, 0.42,
           "magnet.activated", "磁铁持续循环", "循环 1.60 s；随激活结束淡出"),
    _entry("sfx_rocket_boost.wav", sfx_rocket_boost, 1510, False, 0.80,
           "rocket.activated", "火箭启动", "约 1 s 收住，不叠持续轰鸣"),
    _entry("sfx_swallow_ko.wav", sfx_swallow_ko, 1601, False, 0.80,
           "combat.ko-confirmed", "被吞 / KO", "不做爆裂声；随后交给 HUD 状态"),
    _entry("sfx_respawn.wav", sfx_respawn, 1602, False, 0.68,
           "respawn.started", "重生", "保护期状态由 HUD 常驻，不靠本音"),
    _entry("sfx_round_countdown.wav", sfx_round_countdown, 1701, False, 0.62,
           "round.countdown;round.deadline-warning", "倒计时滴答",
           "最后 3 秒可由引擎升 2 半音；每轮只播固定阈值"),
    _entry("sfx_round_start.wav", sfx_round_start, 1702, False, 0.80,
           "round.started", "开局", "每 roundId 一次；音量封顶"),
    _entry("sfx_round_end.wav", sfx_round_end, 1703, False, 0.80,
           "round.results-committed;round.settling", "回合结束/结算",
           "只在结果提交后播；settling 可降调弱化复用"),
    _entry("sfx_threat_warning.wav", sfx_threat_warning, 1801, False, 0.78,
           "threat.approach", "威胁警告（P0）",
           "中频双音，引擎限速；不因低特效被静音"),
    _entry("sfx_ui_click.wav", sfx_ui_click, 1901, False, 0.42,
           "lobby.exit-and-recovery;round.lobby-join;lobby.cosmetic-preview",
           "UI 点击", "2D 非空间化"),
    _entry("sfx_ui_hover.wav", sfx_ui_hover, 1902, False, 0.24,
           "lobby.cosmetic-preview", "UI 悬停", "2D；连续悬停限速"),
)


# ---------------------------------------------------------------------------
# 渲染 / 校验 / 清单
# ---------------------------------------------------------------------------

def render_pcm(entry):
    """返回 (pcm array('h'), float 样本)。所有条目共用，保证测试与产物一致。"""
    x = entry["fn"](entry["seed"])
    if not x:
        raise ValueError("%s: empty buffer" % entry["file"])
    x = normalize(dc_block(x), entry["target"])
    if peak(x) > PEAK_LIMIT:
        raise ValueError("%s: peak above limit" % entry["file"])
    return quantize(x), x


def seam_info(pcm, window=64):
    """循环接缝：|s[0]-s[-1]| 与接缝附近最大相邻样本步长的比值。

    比值 < 1 表示"回绕跳变"比播放中任何一步都小，听感上无法与普通采样相邻点区分。
    与全曲中位数比较没有意义：噪声颗粒本身的逐步变化远大于低频层的一步，
    所以这里只和接缝两侧各 ``window`` 个采样里的最大步长比。
    """
    if len(pcm) < 3:
        return 0.0, 0.0
    delta = abs(pcm[0] - pcm[-1]) / 32768.0
    steps = [abs(pcm[i + 1] - pcm[i]) / 32768.0
             for i in range(max(0, len(pcm) - 1 - window), len(pcm) - 1)]
    steps += [abs(pcm[i + 1] - pcm[i]) / 32768.0
              for i in range(0, min(window, len(pcm) - 1))]
    local_max = max(steps) if steps else 0.0
    ratio = delta / local_max if local_max > 0 else (0.0 if delta == 0 else float("inf"))
    return delta, ratio


def render_all(out_dir, only=None, verbose=True):
    out_dir = os.path.abspath(str(out_dir))
    os.makedirs(out_dir, exist_ok=True)
    rows = []
    for entry in SOUND_TABLE:
        if only and not any(tok in entry["file"] for tok in only):
            continue
        pcm, x = render_pcm(entry)
        path = os.path.join(out_dir, entry["file"])
        write_wav(path, pcm)
        delta, ratio = seam_info(pcm)
        rows.append({
            "file": entry["file"],
            "event_ids": entry["events"],
            "duration_s": "%.3f" % (len(pcm) / float(SR)),
            "loop": "yes" if entry["loop"] else "no",
            "peak_dbfs": "%.2f" % db(peak(x)),
            "generator": entry["fn"].__name__,
            "seed": entry["seed"],
            "rms_dbfs": "%.2f" % db(rms(x)),
            "loop_seam_delta_dbfs": ("-inf" if delta <= 0 else "%.1f" % db(delta)),
            "loop_seam_ratio": ("-" if not entry["loop"] else "%.2f" % ratio),
            "file_bytes": os.path.getsize(path),
        })
        if verbose:
            print("%-26s %5.2fs %-3s peak %6.2f dBFS  %7d B" %
                  (entry["file"], len(pcm) / float(SR),
                   "loop" if entry["loop"] else "1shot",
                   db(peak(x)), os.path.getsize(path)))
    if rows:
        manifest = os.path.join(out_dir, "manifest.csv")
        fields = ("file", "event_ids", "duration_s", "loop", "peak_dbfs",
                  "generator", "seed", "rms_dbfs", "loop_seam_delta_dbfs",
                  "loop_seam_ratio", "file_bytes")
        with open(manifest, "w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=fields)
            w.writeheader()
            w.writerows(rows)
        total = sum(int(r["file_bytes"]) for r in rows)
        if verbose:
            print("\n%d 个文件，合计 %.1f KB；清单 %s" %
                  (len(rows), total / 1024.0, manifest))
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser(description="渲染 VRC塊魂 占位音效")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                  "..", "rendered"))
    ap.add_argument("--only", default="", help="只渲染文件名包含这些子串的条目（逗号分隔）")
    args = ap.parse_args(argv)
    only = [t for t in args.only.split(",") if t]
    render_all(args.out, only or None)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
