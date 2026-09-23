cmake_minimum_required(VERSION 3.22.1)
project(fusionbridge)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

# Tiny JNI helper loaded by the launcher itself before the game's activity is created.
# libmain / libfusion cannot serve this purpose: Unity loads them from inside the
# UnityPlayer constructor, which is exactly the call this library has to precede.
add_library(fusionbridge SHARED
        bridge.cpp
)

target_link_libraries(
        fusionbridge
        PRIVATE
        log
)
