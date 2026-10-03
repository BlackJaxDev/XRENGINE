cmake_minimum_required(VERSION 3.22)

project(XREngineJoltBrowser LANGUAGES C CXX)

if(NOT DEFINED XRE_JOLTC_SOURCE OR NOT EXISTS "${XRE_JOLTC_SOURCE}/CMakeLists.txt")
    message(FATAL_ERROR "XRE_JOLTC_SOURCE must identify the pinned, clean joltc checkout")
endif()

if(NOT DEFINED JOLT_PHYSICS_ROOT OR NOT EXISTS "${JOLT_PHYSICS_ROOT}/Build/CMakeLists.txt")
    message(FATAL_ERROR "JOLT_PHYSICS_ROOT must identify the pinned, clean Jolt checkout")
endif()

add_subdirectory("${XRE_JOLTC_SOURCE}" joltc-upstream)

if(NOT TARGET joltc OR NOT TARGET Jolt)
    message(FATAL_ERROR "Expected single-precision joltc and Jolt static targets")
endif()

# Compile the owned C ABI entry point into the same archive as joltc. Jolt's
# private definitions matter for C++ layout, especially object-layer width.
target_sources(joltc PRIVATE "${CMAKE_CURRENT_LIST_DIR}/jolt-browser-single-thread.cpp")
target_compile_definitions(joltc PRIVATE $<TARGET_PROPERTY:Jolt,COMPILE_DEFINITIONS>)
