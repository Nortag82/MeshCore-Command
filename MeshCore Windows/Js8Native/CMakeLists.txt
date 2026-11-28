cmake_minimum_required(VERSION 3.22)
project(MeshCoreJs8Phy LANGUAGES CXX)

set(JS8_SOURCE "" CACHE PATH "Path to the JS8Call-improved v3.0.3 checkout")
find_package(Git REQUIRED)
execute_process(COMMAND "${GIT_EXECUTABLE}" -C "${JS8_SOURCE}" rev-parse HEAD
    OUTPUT_VARIABLE upstream OUTPUT_STRIP_TRAILING_WHITESPACE RESULT_VARIABLE git_status)
if(NOT git_status EQUAL 0 OR NOT upstream STREQUAL "4c592bd9a034f18178a3e7179db92acb14939668")
    message(FATAL_ERROR "JS8_SOURCE must point to the pinned JS8Call-improved v3.0.3 checkout.")
endif()
execute_process(COMMAND "${GIT_EXECUTABLE}" -C "${JS8_SOURCE}" diff --quiet HEAD --
    RESULT_VARIABLE upstream_modified)
if(NOT upstream_modified EQUAL 0)
    message(FATAL_ERROR "The pinned upstream checkout contains tracked modifications.")
endif()

find_package(Qt6 REQUIRED COMPONENTS Core)
find_package(Boost CONFIG REQUIRED)
find_path(FFTW_INCLUDE_DIR fftw3.h REQUIRED)
find_library(FFTW_FLOAT_LIBRARY NAMES fftw3f REQUIRED)

add_executable(meshcore-js8-phy main.cpp
    "${JS8_SOURCE}/JS8_Mode/JS8.h"
    "${JS8_SOURCE}/JS8_Mode/JS8.cpp"
    "${JS8_SOURCE}/JS8_Mode/FrequencyTracker.cpp"
    "${JS8_SOURCE}/JS8_Mode/DecodedText.cpp"
    "${JS8_SOURCE}/JS8_Main/Varicode.h"
    "${JS8_SOURCE}/JS8_Main/Varicode.cpp"
    "${JS8_SOURCE}/JS8_JSC/JSC.cpp"
    "${JS8_SOURCE}/JS8_JSC/JSC_list.cpp"
    "${JS8_SOURCE}/JS8_JSC/JSC_map.cpp")
set_target_properties(meshcore-js8-phy PROPERTIES AUTOMOC ON)
target_compile_features(meshcore-js8-phy PRIVATE cxx_std_20)
target_compile_definitions(meshcore-js8-phy PRIVATE NOMINMAX)
target_include_directories(meshcore-js8-phy SYSTEM PRIVATE "${JS8_SOURCE}" "${FFTW_INCLUDE_DIR}")
target_link_libraries(meshcore-js8-phy PRIVATE Qt6::Core Boost::headers "${FFTW_FLOAT_LIBRARY}")
