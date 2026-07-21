#!/usr/bin/env bash
# Origin: vendored verbatim from sharpemu/FFmpeg scripts/build-sharpemu-ffmpeg.sh (Bink2/BK2 decoder fork).
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${SHARPEMU_FFMPEG_BUILD_DIR:-${root_dir}/artifacts/build}"
install_dir="${1:-${root_dir}/artifacts/install}"

mkdir -p "${build_dir}" "${install_dir}"
cd "${build_dir}"

"${root_dir}/configure" \
    --prefix="${install_dir}" \
    --disable-autodetect \
    --disable-debug \
    --disable-doc \
    --disable-ffplay \
    --disable-ffprobe \
    --disable-network \
    --disable-shared \
    --enable-static \
    --enable-protocol=file \
    --enable-protocol=pipe

if command -v nproc >/dev/null 2>&1; then
    jobs="$(nproc)"
elif command -v sysctl >/dev/null 2>&1; then
    jobs="$(sysctl -n hw.logicalcpu)"
else
    jobs=2
fi

make -j"${jobs}"
make install
