cmake_minimum_required(VERSION 3.22)
project(perf_watchdog LANGUAGES CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "perf_watchdog Windows plugin requires Windows x64")
endif()

add_library(perf_watchdog SHARED perf_watchdog_windows.cpp)
target_compile_features(perf_watchdog PRIVATE cxx_std_17)
target_compile_definitions(perf_watchdog PRIVATE _WIN32_WINNT=0x0602 WINVER=0x0602)
if(MSVC)
    target_compile_options(perf_watchdog PRIVATE /W4 /guard:cf /ZH:SHA_256)
    target_link_options(perf_watchdog PRIVATE /guard:cf)
endif()
