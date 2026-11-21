# Builds the XRENGINE block-compression shim (xre_bcn) from the pinned bc7enc_rdo sources.
# Build-Bc7Enc.ps1 passes BC7ENC_SOURCE_DIR. Only the MIT/Unlicense sources are compiled.
cmake_minimum_required(VERSION 3.16)
project(xre_bcn LANGUAGES CXX)

if(NOT BC7ENC_SOURCE_DIR)
    message(FATAL_ERROR "Set BC7ENC_SOURCE_DIR to the bc7enc_rdo source checkout.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(xre_bcn SHARED
    ${CMAKE_CURRENT_LIST_DIR}/xre_bcn.cpp
    ${BC7ENC_SOURCE_DIR}/bc7enc.cpp
    ${BC7ENC_SOURCE_DIR}/rgbcx.cpp)

target_include_directories(xre_bcn PRIVATE ${BC7ENC_SOURCE_DIR})

if(MSVC)
    target_compile_options(xre_bcn PRIVATE /O2 /fp:fast /W3)
    target_compile_definitions(xre_bcn PRIVATE _CRT_SECURE_NO_WARNINGS)
else()
    target_compile_options(xre_bcn PRIVATE -O3 -fno-strict-aliasing)
endif()
