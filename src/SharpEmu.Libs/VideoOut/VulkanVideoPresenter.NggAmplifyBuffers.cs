// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private void BuildNggAmplifyDrawResources(
            TranslatedDrawResources resources,
            byte[] captureSpirv,
            NggAmplifyCapture captureLayout,
            IReadOnlyList<VulkanGuestMemoryBuffer> captureInputs,
            uint invocationCount)
        {
            // NGG amplify bridge: implemented in a later pass.
        }
    }
}
