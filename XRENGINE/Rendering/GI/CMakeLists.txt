cmake_minimum_required(VERSION 3.16)
project(RestirGINative)

# Set C++14 standard
set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Find required packages
find_package(OpenGL REQUIRED)
find_package(GLEW REQUIRED)

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
