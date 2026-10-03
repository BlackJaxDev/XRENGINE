cmake_minimum_required(VERSION 3.16)
project(RestirGINative)

# Set C++14 standard
set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include(FetchContent)

# Find required packages
find_package(OpenGL REQUIRED)
find_package(GLEW QUIET)

# Fallback: fetch and build GLEW locally if the host does not provide it.
if (NOT GLEW_FOUND)
    message(STATUS "GLEW not found; fetching release glew-2.2.0 for local build")

    FetchContent_Declare(
        glew
        URL https://github.com/nigels-com/glew/releases/download/glew-2.2.0/glew-2.2.0.zip
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    )

    FetchContent_MakeAvailable(glew)

    if (NOT TARGET GLEW::GLEW)
        # Build a minimal static GLEW target from source; keep it namespaced for consumers.
        add_library(glew_local STATIC ${glew_SOURCE_DIR}/src/glew.c)
        target_include_directories(glew_local PUBLIC ${glew_SOURCE_DIR}/include)
        target_compile_definitions(glew_local PUBLIC GLEW_STATIC)
        if (WIN32)
            target_link_libraries(glew_local PUBLIC opengl32)
        endif()
        add_library(GLEW::GLEW ALIAS glew_local)
    endif()
endif()

if (NOT TARGET GLEW::GLEW)
    message(FATAL_ERROR "GLEW dependency is unavailable; install it or allow FetchContent to download it.")
endif()

# Create shared library (DLL)
add_library(RestirGINative SHARED
    RestirGI.Native.cpp
)

# Link libraries
target_link_libraries(RestirGINative
    PRIVATE
    OpenGL::GL
    GLEW::GLEW
)

# Set target properties for DLL export
set_target_properties(RestirGINative PROPERTIES
    WINDOWS_EXPORT_ALL_SYMBOLS ON
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
)

# Add compiler definitions for Windows DLL
if (WIN32)
    target_compile_definitions(RestirGINative PRIVATE
        GLEW_STATIC
    )
endif()
