#!/usr/bin/env python3
"""
Bakes an equirectangular HDR/EXR panoramic image into a 6-face HDR environment cubemap asset (.jtex).
Follows Vulkan/DirectX cubemap conventions matching Jatayu's IBL compute shaders.
"""

import os
import sys
import argparse
import hashlib
import struct
import time
import numpy as np

# Header layout matching CookedTextureHeader (Pack = 1):
# uint32 Magic (0x5845544A)
# uint32 Version (1)
# uint64 GuidLow, uint64 GuidHigh
# uint32 Width
# uint32 Height
# uint32 MipLevels
# uint32 ResidentMips
# uint8  Format (9 = Rgba16Float, 10 = Rgba32Float)
# uint8  Reserved1
# uint16 Reserved2
# uint32 Flags (8 = Cubemap)
# uint64 ResidentTailOffset
# uint32 ResidentTailSize
HEADER_FORMAT = "<IIQQIIIIBBHIQI"

def compute_guid(path: str):
    h = hashlib.md5(path.encode("utf-8")).digest()
    low, high = struct.unpack("<QQ", h)
    return low, high

def load_equirect_image(path: str):
    if not os.path.isfile(path):
        raise FileNotFoundError(f"Source HDRI file not found: {path}")

    # Try OpenImageIO first
    try:
        import OpenImageIO as oiio
        img = oiio.ImageBuf(path)
        spec = img.spec()
        w = spec.width
        h = spec.height
        ch = spec.nchannels
        pixels = np.array(img.get_pixels(oiio.TypeFloat), dtype=np.float32).reshape((h, w, ch))
        if ch == 3:
            # Add alpha channel
            alpha = np.ones((h, w, 1), dtype=np.float32)
            pixels = np.concatenate([pixels, alpha], axis=-1)
        elif ch > 4:
            pixels = pixels[..., :4]
        return pixels, w, h
    except Exception as e:
        pass

    # Try OpenEXR
    try:
        import OpenEXR, Imath
        exr = OpenEXR.InputFile(path)
        header = exr.header()
        dw = header["dataWindow"]
        w = dw.max.x - dw.min.x + 1
        h = dw.max.y - dw.min.y + 1
        pt = Imath.PixelType(Imath.PixelType.FLOAT)
        ch_names = header["channels"].keys()
        r_str = exr.channel("R", pt) if "R" in ch_names else b"\x00" * (w * h * 4)
        g_str = exr.channel("G", pt) if "G" in ch_names else b"\x00" * (w * h * 4)
        b_str = exr.channel("B", pt) if "B" in ch_names else b"\x00" * (w * h * 4)
        a_str = exr.channel("A", pt) if "A" in ch_names else None

        r = np.frombuffer(r_str, dtype=np.float32).reshape((h, w))
        g = np.frombuffer(g_str, dtype=np.float32).reshape((h, w))
        b = np.frombuffer(b_str, dtype=np.float32).reshape((h, w))
        if a_str is not None:
            a = np.frombuffer(a_str, dtype=np.float32).reshape((h, w))
        else:
            a = np.ones((h, w), dtype=np.float32)
        pixels = np.stack([r, g, b, a], axis=-1)
        return pixels, w, h
    except Exception as e:
        raise RuntimeError(f"Failed to load HDR image '{path}': {e}")

def bake_cubemap(src_pixels: np.ndarray, w_src: int, h_src: int, resolution: int = 512):
    N = resolution
    st = np.linspace(-1.0 + 1.0 / N, 1.0 - 1.0 / N, N, dtype=np.float32)
    sx, sy = np.meshgrid(st, st)

    # 6 faces matching CubeDirection in ibl_environment.comp:
    # Face 0: +X (+X, -Y, -Z)
    # Face 1: -X (-X, -Y, +Z)
    # Face 2: +Y (+X, +Y, +Z)
    # Face 3: -Y (+X, -Y, -Z)
    # Face 4: +Z (+X, -Y, +Z)
    # Face 5: -Z (-X, -Y, -Z)
    faces_dir = [
        np.stack([np.ones_like(sx), -sy, -sx], axis=-1),  # Face 0: +X
        np.stack([-np.ones_like(sx), -sy,  sx], axis=-1), # Face 1: -X
        np.stack([sx,  np.ones_like(sx),  sy], axis=-1),  # Face 2: +Y
        np.stack([sx, -np.ones_like(sx), -sy], axis=-1),  # Face 3: -Y
        np.stack([sx, -sy,  np.ones_like(sx)], axis=-1),  # Face 4: +Z
        np.stack([-sx, -sy, -np.ones_like(sx)], axis=-1), # Face 5: -Z
    ]

    cubemap_faces = []
    for d in faces_dir:
        norm = np.linalg.norm(d, axis=-1, keepdims=True)
        d_norm = d / np.maximum(norm, 1e-7)
        x = d_norm[..., 0]
        y = d_norm[..., 1]
        z = d_norm[..., 2]

        phi = np.arctan2(z, x)
        theta = np.arcsin(np.clip(y, -1.0, 1.0))

        # Equirectangular UV mapping (top = zenith, bottom = nadir)
        u = (phi / (2.0 * np.pi) + 0.5) * w_src - 0.5
        v = (0.5 - theta / np.pi) * h_src - 0.5

        u0 = np.floor(u).astype(np.int32) % w_src
        v0 = np.clip(np.floor(v).astype(np.int32), 0, h_src - 1)
        u1 = (u0 + 1) % w_src
        v1 = np.clip(v0 + 1, 0, h_src - 1)

        du = (u - np.floor(u))[..., np.newaxis]
        dv = (v - np.floor(v))[..., np.newaxis]

        sampled = (
            src_pixels[v0, u0] * (1.0 - du) * (1.0 - dv) +
            src_pixels[v0, u1] * du * (1.0 - dv) +
            src_pixels[v1, u0] * (1.0 - du) * dv +
            src_pixels[v1, u1] * du * dv
        )
        cubemap_faces.append(sampled)

    # Shape: (6, resolution, resolution, 4)
    return np.stack(cubemap_faces, axis=0)

def main():
    parser = argparse.ArgumentParser(description="Bake HDR/EXR equirectangular image into 512x512 cubemap asset.")
    parser.add_argument("input", help="Input .exr or .hdr file path")
    parser.add_argument("output", help="Output .jtex or .raw cubemap file path")
    parser.add_argument("-r", "--resolution", type=int, default=512, help="Cubemap face resolution (default: 512)")
    parser.add_argument("-f", "--format", choices=["rgba16f", "rgba32f"], default="rgba16f", help="Output float format")
    args = parser.parse_args()

    t0 = time.time()
    print(f"[HdrCooker] Loading source HDR: {args.input}...")
    src_pixels, w_src, h_src = load_equirect_image(args.input)
    print(f"[HdrCooker] Loaded {w_src}x{h_src} equirectangular image ({time.time() - t0:.2f}s).")

    print(f"[HdrCooker] Baking 6 faces at {args.resolution}x{args.resolution} with bilinear interpolation...")
    t_bake = time.time()
    cubemap = bake_cubemap(src_pixels, w_src, h_src, args.resolution)
    print(f"[HdrCooker] Projection complete ({time.time() - t_bake:.2f}s). Value range: min={cubemap.min():.4f}, max={cubemap.max():.2f}")

    # Prepare payload bytes
    if args.format == "rgba16f":
        # Clamp to max float16 finite value to prevent inf
        cubemap_data = np.clip(cubemap, 0.0, 65504.0).astype(np.float16)
        format_id = 9 # Rgba16Float
        payload_bytes = cubemap_data.tobytes()
    else:
        cubemap_data = cubemap.astype(np.float32)
        format_id = 10 # Rgba32Float
        payload_bytes = cubemap_data.tobytes()

    magic = 0x5845544A # "JTEX"
    version = 1
    guid_low, guid_high = compute_guid(args.output)
    width = args.resolution
    height = args.resolution
    mip_levels = 1
    resident_mips = 1
    flags = 8 # Cubemap (1 << 3)
    resident_tail_offset = 0
    resident_tail_size = 0

    header_bytes = struct.pack(
        HEADER_FORMAT,
        magic,
        version,
        guid_low,
        guid_high,
        width,
        height,
        mip_levels,
        resident_mips,
        format_id,
        0, # Reserved1
        0, # Reserved2
        flags,
        resident_tail_offset,
        resident_tail_size
    )

    out_dir = os.path.dirname(args.output)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    with open(args.output, "wb") as f:
        f.write(header_bytes)
        f.write(payload_bytes)

    total_bytes = len(header_bytes) + len(payload_bytes)
    total_time = time.time() - t0
    print(f"[HdrCooker] Successfully baked '{args.output}'!")
    print(f"            Faces: 6x {args.resolution}x{args.resolution} {args.format.upper()}")
    print(f"            Payload: {total_bytes:,} bytes")
    print(f"            Total elapsed: {total_time:.2f}s")

if __name__ == "__main__":
    main()
