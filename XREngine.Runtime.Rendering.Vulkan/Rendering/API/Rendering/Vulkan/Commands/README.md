# Vulkan Commands

Owns command-buffer allocation, recording, command-chain scheduling, frame
operation queues/signatures, blits, readbacks, indirect draw, render-state
application, dirty tracking, and one-time submit helpers. Resource planning,
persistent resource allocation, and wrapper objects live elsewhere. Command
recording consumes prepared resource plans and must not allocate persistent
images or buffers.
