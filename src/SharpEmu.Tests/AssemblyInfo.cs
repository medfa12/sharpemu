// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

// Several presenter tests exercise VulkanVideoPresenter's process-wide static
// image-tracking state through its test hooks. Running test collections in
// parallel lets those mutations race across classes and fail intermittently on
// machines with more cores than the author's. Serialize the whole assembly so
// the shared static state is only touched by one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
