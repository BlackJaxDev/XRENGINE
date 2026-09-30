#include <Jolt/Jolt.h>
#include <Jolt/Core/JobSystemSingleThreaded.h>
#include <joltc.h>

extern "C" JPH_JobSystem* XRE_JPH_JobSystemSingleThreaded_Create(unsigned int maxJobs)
{
    return reinterpret_cast<JPH_JobSystem*>(new JPH::JobSystemSingleThreaded(maxJobs));
}
