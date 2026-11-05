cmake_minimum_required(VERSION 3.22.1)
project(perf_watchdog)

add_library(perf_watchdog SHARED perf_watchdog_android.cpp)
target_compile_features(perf_watchdog PRIVATE cxx_std_17)
target_compile_options(perf_watchdog PRIVATE -fno-omit-frame-pointer -fvisibility=hidden)
target_link_libraries(perf_watchdog PRIVATE dl log)
