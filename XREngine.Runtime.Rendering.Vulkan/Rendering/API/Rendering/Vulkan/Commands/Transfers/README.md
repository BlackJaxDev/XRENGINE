# Vulkan Command Transfers

Owns command-buffer recording for texture uploads, buffer/image copies, and
blits plus publication state for recorded uploads. Staging allocation and
persistent upload ownership live under `Resources/Uploads`; transfer recording
must not create a second upload queue or lifetime authority.
