cmake_minimum_required(VERSION 3.22.1)
project(fusionunwind C)

# Exports the LLVM unwinder (_Unwind_*) as a shared library.
# MonoMod's native helper takes the unwinder from the process's global symbols, which on a device come from
# the libraries app_process starts with. A process whose arm64 code runs under binary translation has no such
# libraries, so tools/AndroidDotnetPatch adds this library to the helper's dependencies. On a device the global
# copy is still found first. The NDK ships the unwinder with hidden visibility; a copy of the archive gets
# default visibility for the _Unwind_* entry points.

if(ANDROID_ABI STREQUAL "arm64-v8a")
    set(UNWIND_ARCH aarch64)
elseif(ANDROID_ABI STREQUAL "armeabi-v7a")
    set(UNWIND_ARCH arm)
elseif(ANDROID_ABI STREQUAL "x86_64")
    set(UNWIND_ARCH x86_64)
else()
    set(UNWIND_ARCH i386)
endif()

file(GLOB UNWIND_ARCHIVE "${ANDROID_TOOLCHAIN_ROOT}/lib/clang/*/lib/linux/${UNWIND_ARCH}/libunwind.a")
list(LENGTH UNWIND_ARCHIVE UNWIND_ARCHIVE_COUNT)
if(NOT UNWIND_ARCHIVE_COUNT EQUAL 1)
    message(FATAL_ERROR "Expected one NDK libunwind.a for ${UNWIND_ARCH}, found: ${UNWIND_ARCHIVE}")
endif()

set(UNWIND_OBJCOPY "${ANDROID_TOOLCHAIN_ROOT}/bin/llvm-objcopy${ANDROID_TOOLCHAIN_SUFFIX}")
set(UNWIND_EXPORTED "${CMAKE_CURRENT_BINARY_DIR}/libfusionunwind_impl.a")

add_custom_command(
        OUTPUT ${UNWIND_EXPORTED}
        COMMAND ${UNWIND_OBJCOPY}
                "--set-symbols-visibility=${CMAKE_CURRENT_SOURCE_DIR}/unwind_symbols.txt=default"
                ${UNWIND_ARCHIVE} ${UNWIND_EXPORTED}
        DEPENDS ${UNWIND_ARCHIVE} ${CMAKE_CURRENT_SOURCE_DIR}/unwind_symbols.txt
        VERBATIM
)
add_custom_target(fusionunwind_archive DEPENDS ${UNWIND_EXPORTED})

add_library(fusionunwind SHARED
        unwind.c
)
add_dependencies(fusionunwind fusionunwind_archive)

target_link_options(
        fusionunwind
        PRIVATE
        "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports.map"
        "-Wl,--whole-archive" ${UNWIND_EXPORTED} "-Wl,--no-whole-archive"
)
set_target_properties(fusionunwind PROPERTIES LINK_DEPENDS
        "${CMAKE_CURRENT_SOURCE_DIR}/exports.map;${UNWIND_EXPORTED}")
