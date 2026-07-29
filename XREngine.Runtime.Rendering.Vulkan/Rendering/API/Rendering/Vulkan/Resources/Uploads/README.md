# Vulkan Resource Uploads

Owns imported-texture upload contracts, scheduling, staging preparation,
transfer recording/submission, completion polling, publication, and queue
policy. Prepared GPU resources are published only after transfer completion;
superseded resources enter normal retirement.
