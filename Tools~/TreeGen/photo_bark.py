# Кора из фото-скана вместо процедурной (Tools/TreeGen): процедурная кора сгенерированного дуба (oak_textures.py — пластины
# по сетке) вблизи читалась нарисованной «плиткой». Здесь набор Poly Haven (CC0: <set>_diff, _nor_gl, _arm, _disp — 2K
# JPG) переводится в набор коры NatureManufacture для той же плитки, что у меша (UV ствола генератора — плитка по
# окружности и вдоль, BARK_TILE): альбедо sRGB, нормаль OpenGL как есть, маска R металличность 0, G AO (ARM), B высота
# (смещение), A гладкость (1 − шероховатость ARM). Весь скан (он бесшовный) сжимается в плитку — рисунок мельче, если скан
# снят на больший кусок (jolcham_oak_bark_01 — 1 × 2 м на плитку дуба 0.6 × 1.2 м: борозды ~0.6 настоящих), зато без
# шва по окружности. Цвет — в палитру сцены (envgen_common.grade_albedo в numpy: насыщенность × --saturation, средний —
# --mean, линейный), как была откалибрована процедурная кора. --grey-rect u0 v0 u1 v1 (UV, v снизу): этот кусок скана —
# выветренная серая древесина (у сосны — пластина deadwood_rect, на неё развёрнуты сухие сучья и обломки): рисунок скана
# без цвета, на --grey яркости коры вокруг, край рваный, --feather пикселей. Кусок старой текстуры (серебристая пластина)
# стоял на стволе светлым квадратом, поэтому серый здесь — лишь чуть светлее коры.
# --crop W H --scan W H (м): вместо сжатия всего скана — кусок плитки W × H из скана в настоящем масштабе, сшитый в
# бесшовный (у каждого края — плавный переход в скан, что за противоположным краем куска, на --blend пикселей скана).
# Сосновую кору Poly Haven (2 × 2 м), сжатую в плитку 0.6 × 1.2 м, пластины становились мельче втрое и ствол — гладким.
# Запуск (без Blender): py -I photo_bark.py <папка набора> <префикс выхода, напр. .../oak_bark> --size 1024 2048
#   --mean 0.100 0.087 0.070 --saturation 0.7 [--grey-rect u0 v0 u1 v1] [--crop 0.6 1.2 --scan 2 2]
import argparse
import os

import numpy as np
from PIL import Image


CROP = None  # (ширина, высота куска в пикселях скана, переход)


def seamless_crop(a, cw, ch, blend):
    """Кусок cw × ch из угла бесшовного скана a, сам ставший бесшовным: у левого (верхнего) края — переход в то, что в
    скане за правым (нижним) краем куска, поэтому край куска продолжается в начало."""
    out = a[:ch + blend, :cw + blend].astype(np.float32)
    t = np.clip(1 - np.arange(blend, dtype=np.float32) / blend, 0, 1)
    t = t * t * (3 - 2 * t)
    shape = (1, blend) + (1,) * (out.ndim - 2)
    out[:, :blend] = out[:, :blend] * (1 - t.reshape(shape)) + out[:, cw:cw + blend] * t.reshape(shape)
    shape = (blend, 1) + (1,) * (out.ndim - 2)
    out[:blend] = out[:blend] * (1 - t.reshape(shape)) + out[ch:ch + blend] * t.reshape(shape)
    return out[:ch, :cw]


def load(path, size, mode='RGB'):
    image = Image.open(path).convert(mode)
    if CROP:
        cw, ch, blend = CROP
        a = seamless_crop(np.asarray(image), cw, ch, blend)
        image = Image.fromarray(np.clip(np.round(a), 0, 255).astype(np.uint8), mode)
    return np.asarray(image.resize(size, Image.LANCZOS), np.float32) / 255.0


def to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def grade(rgb, target, saturation):
    lum = rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    out = lum[..., None] + (rgb - lum[..., None]) * saturation
    gain = np.asarray(target, np.float32) / np.maximum(out.reshape(-1, 3).mean(0), 1e-4)
    return np.clip(out * gain, 0.0, 1.0)


def rect_pixels(size, rect):
    w, h = size
    return (int(np.floor(rect[0] * w)), int(np.floor((1 - rect[3]) * h)), int(np.ceil(rect[2] * w)), int(np.ceil((1 - rect[1]) * h)))


def keep_weight(size, rect, feather, seed=7):
    """1 внутри rect (UV, v снизу), к 0 за feather пикселей снаружи; край рваный (шум)."""
    w, h = size
    x0, y0, x1, y1 = rect_pixels(size, rect)
    yy, xx = np.mgrid[0:h, 0:w]
    dx = np.maximum(np.maximum(x0 - xx, xx - (x1 - 1)), 0)
    dy = np.maximum(np.maximum(y0 - yy, yy - (y1 - 1)), 0)
    rng = np.random.default_rng(seed)
    coarse = rng.random((h // 8 + 2, w // 8 + 2)).astype(np.float32)
    noise = np.asarray(Image.fromarray((coarse * 255).astype(np.uint8)).resize((w + 16, h + 16), Image.BICUBIC), np.float32)[:h, :w] / 255
    dist = np.hypot(dx, dy) + (noise - 0.5) * feather
    return np.clip(1 - dist / max(feather, 1), 0, 1).astype(np.float32)


def save(array, path):
    Image.fromarray(np.round(np.clip(array, 0, 1) * 255).astype(np.uint8)).save(path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('source')
    parser.add_argument('prefix')
    parser.add_argument('--size', nargs=2, type=int, default=[1024, 2048])
    parser.add_argument('--mean', nargs=3, type=float, required=True)
    parser.add_argument('--saturation', type=float, default=0.7)
    parser.add_argument('--grey-rect', nargs=4, type=float)
    parser.add_argument('--grey', type=float, default=1.2)
    parser.add_argument('--feather', type=float, default=14)
    parser.add_argument('--crop', nargs=2, type=float)
    parser.add_argument('--scan', nargs=2, type=float)
    parser.add_argument('--blend', type=int, default=160)
    args = parser.parse_args()
    name = os.path.basename(os.path.normpath(args.source))
    base = os.path.join(args.source, name)
    size = tuple(args.size)
    if args.crop and args.scan:
        global CROP
        scan_w, scan_h = Image.open(base + '_diff.jpg').size
        CROP = (int(round(args.crop[0] / args.scan[0] * scan_w)), int(round(args.crop[1] / args.scan[1] * scan_h)), args.blend)
    albedo = grade(to_linear(load(base + '_diff.jpg', size)), args.mean, args.saturation)
    normal = load(base + '_nor_gl.jpg', size)
    arm = load(base + '_arm.jpg', size)
    height = load(base + '_disp.jpg', size, 'L')
    lo, hi = np.percentile(height, (1, 99))
    height = np.clip((height - lo) / max(hi - lo, 1e-3), 0, 1)
    mask = np.stack([np.zeros_like(height), arm[..., 0], height, 1.0 - arm[..., 1]], -1)
    albedo = to_srgb(albedo)
    if args.grey_rect:
        x0, y0, x1, y1 = rect_pixels(size, args.grey_rect)
        linear = to_linear(albedo)
        lum = linear @ np.array([0.2126, 0.7152, 0.0722], np.float32)
        tone = float(lum.mean()) * args.grey * np.array([1.1, 1.0, 0.86], np.float32)
        patch_mean = max(float(lum[y0:y1, x0:x1].mean()), 1e-4)
        grey = np.clip(lum[..., None] / patch_mean * tone, 0, 1)
        w = keep_weight(size, args.grey_rect, args.feather)[..., None]
        albedo = to_srgb(linear * (1 - w) + grey * w)
    save(albedo, args.prefix + '_albedo.png')
    save(normal, args.prefix + '_normal.png')
    Image.fromarray(np.round(np.clip(mask, 0, 1) * 255).astype(np.uint8), 'RGBA').save(args.prefix + '_mask.png')
    mean = to_linear(albedo).reshape(-1, 3).mean(0)
    print('PHOTO_BARK', args.prefix, 'linear mean', np.round(mean, 3))


main()
