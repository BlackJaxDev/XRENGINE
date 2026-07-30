# Vulkan Command Scheduling

Owns command-chain nodes, dependencies, queue eligibility, schedules, cache
keys, and dirty reasons. Scheduling decides order and reuse; it does not emit
Vulkan commands or mutate render-graph/resource owners.
